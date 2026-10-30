package authority

import (
	"bytes"
	"crypto/ecdsa"
	"encoding/json/jsontext"
	"io"
	"net"
	"net/http"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	bolt "go.etcd.io/bbolt"
)

type httpFixture struct {
	server                                *ControlServer
	store                                 *Store
	url                                   string
	owner, visitor, publisher             *DeviceIdentity
	ownerHTTP, visitorHTTP, publisherHTTP *http.Client
	scope                                 AccessScope
}
type wireReply struct {
	Kind    string         `json:"kind"`
	Version int            `json:"apiVersion"`
	ID      string         `json:"requestId"`
	Result  jsontext.Value `json:"result,omitempty"`
	Error   *controlError  `json:"error,omitempty"`
}

func newHTTPFixture(t *testing.T) *httpFixture {
	t.Helper()
	base := newStoreFixture(t)
	root, key := rootFixture(t)
	identity := deviceFixture(t, root, key)
	owner := deviceFixture(t, root, key)
	visitor := deviceFixture(t, root, key)
	publisher := deviceFixture(t, root, key)
	db, err := bolt.Open(filepath.Join(base.directory, "http-authority.db"), 0600, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	witnessDB, err := bolt.Open(filepath.Join(base.directory, "http-witness.db"), 0600, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = witnessDB.Close() })
	witness, err := NewBoltWitness(witnessDB, true)
	if err != nil {
		t.Fatal(err)
	}
	store, err := NewStore(db, witness, identity.ID, hash(root.Raw), owner.ID, time.Now(), true)
	if err != nil {
		t.Fatal(err)
	}
	server, err := NewControlServer(identity, store, time.Now)
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	finished := make(chan struct{})
	go func() { defer close(finished); _ = server.Serve(listener) }()
	t.Cleanup(func() { _ = server.Close(); _ = listener.Close(); <-finished })
	client := func(device *DeviceIdentity) *http.Client {
		cfg, err := device.ClientTLS(identity.ID, time.Now)
		if err != nil {
			t.Fatal(err)
		}
		transport := &http.Transport{TLSClientConfig: cfg, MaxConnsPerHost: 2}
		t.Cleanup(transport.CloseIdleConnections)
		return &http.Client{Transport: transport, Timeout: 3 * time.Second}
	}
	f := &httpFixture{server: server, store: store, url: "https://" + listener.Addr().String() + "/control/v1", owner: owner, visitor: visitor, publisher: publisher, ownerHTTP: client(owner), visitorHTTP: client(visitor)}
	f.publisherHTTP = client(publisher)
	f.scope = AccessScope{VisitorDevice: visitor.ID, VisitorApp: "org.example.http-client", Target: ServiceScope{PublisherDevice: publisher.ID, PublisherApp: "org.example.http-publisher", Service: "file"}}
	for _, value := range []struct {
		id    string
		roles Roles
	}{{visitor.ID, Roles{Access: true}}, {publisher.ID, Roles{Publish: true}}} {
		reply := f.call(t, f.ownerHTTP, "owner.device.set", map[string]any{"device": value.id, "roles": value.roles, "expectedRevision": Revision(0), "active": true}, uuid.NewString())
		if reply.Error != nil {
			t.Fatal(reply.Error.Code)
		}
	}
	return f
}
func (f *httpFixture) call(t *testing.T, client *http.Client, method string, parameters any, operation string) wireReply {
	t.Helper()
	id := uuid.NewString()
	input := map[string]any{"kind": "request", "apiVersion": 1, "requestId": id, "method": method, "params": parameters}
	if operation != "" {
		input["operationId"] = operation
	}
	body, err := encode(input)
	if err != nil {
		t.Fatal(err)
	}
	response, err := client.Post(f.url, "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	payload, err := io.ReadAll(io.LimitReader(response.Body, 65537))
	if err != nil || response.StatusCode != 200 {
		t.Fatalf("unexpected control HTTP status %d", response.StatusCode)
	}
	var result wireReply
	if decode(payload, &result) != nil || result.ID != id || result.Kind != "response" || result.Version != 1 || (result.Error == nil) == (len(result.Result) == 0) {
		t.Fatal("invalid correlated reply")
	}
	return result
}
func replyCode(t *testing.T, reply wireReply, code string) {
	t.Helper()
	if reply.Error == nil || reply.Error.Code != code || len(reply.Result) != 0 {
		t.Fatalf("expected control refusal %s", code)
	}
}
func (f *httpFixture) grant(t *testing.T) string {
	t.Helper()
	reply := f.call(t, f.ownerHTTP, "owner.service.set", map[string]any{"scope": f.scope.Target, "expectedRevision": Revision(0), "protocol": "https", "authority": "files.example.invalid", "active": true}, uuid.NewString())
	if reply.Error != nil {
		t.Fatal(reply.Error.Code)
	}
	operation := uuid.NewString()
	reply = f.call(t, f.ownerHTTP, "owner.grant.set", map[string]any{"scope": f.scope, "expectedRevision": Revision(0), "active": true}, operation)
	if reply.Error != nil {
		t.Fatal(reply.Error.Code)
	}
	return operation
}
func TestActualControlTlsOwnerGrantAndVisitorBoundLease(t *testing.T) {
	f := newHTTPFixture(t)
	parameters := map[string]any{"visitorApp": f.scope.VisitorApp, "target": f.scope.Target}
	replyCode(t, f.call(t, f.visitorHTTP, "lease.acquire", parameters, ""), "AUTH_REQUIRED")
	operation := f.grant(t)
	reply := f.call(t, f.visitorHTTP, "lease.acquire", parameters, "")
	if reply.Error != nil {
		t.Fatal(reply.Error.Code)
	}
	var lease leaseResult
	if decode(reply.Result, &lease) != nil {
		t.Fatal("invalid lease result")
	}
	current, err := f.store.Snapshot(f.scope, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if lease.Current != current {
		t.Fatal("acquisition omitted authenticated current authority")
	}
	key := f.server.identity.Leaf.PublicKey.(*ecdsa.PublicKey)
	receipt, err := VerifyLease(lease.Token, key, f.server.identity.ID, f.server.issuer.Epoch, current, time.Now(), 100)
	if err != nil || receipt.Scope.VisitorDevice != f.visitor.ID {
		t.Fatal("actual visitor lease invalid")
	}
	validated := f.call(t, f.publisherHTTP, "lease.validate", map[string]any{"token": lease.Token}, "")
	if validated.Error != nil {
		t.Fatal(validated.Error.Code)
	}
	var validation validationResult
	if decode(validated.Result, &validation) != nil || validation.Current != current || validation.ServerEpoch != f.server.issuer.Epoch {
		t.Fatal("publisher validation omitted current authenticated authority")
	}
	replyCode(t, f.call(t, f.visitorHTTP, "lease.validate", map[string]any{"token": lease.Token}, ""), "AUTH_REQUIRED")
	badToken := lease.Token[:len(lease.Token)-2] + "AA"
	if badToken == lease.Token {
		badToken = lease.Token[:len(lease.Token)-2] + "BA"
	}
	replyCode(t, f.call(t, f.publisherHTTP, "lease.validate", map[string]any{"token": badToken}, ""), "AUTH_REQUIRED")
	forged := map[string]any{"visitorApp": f.scope.VisitorApp, "target": f.scope.Target, "visitorDevice": f.owner.ID}
	replyCode(t, f.call(t, f.visitorHTTP, "lease.acquire", forged, ""), "INVALID_REQUEST")
	replyCode(t, f.call(t, f.visitorHTTP, "owner.device.set", map[string]any{"device": f.visitor.ID, "expectedRevision": Revision(1), "roles": Roles{Owner: true}, "active": true}, uuid.NewString()), "AUTH_REQUIRED")
	reply = f.call(t, f.ownerHTTP, "owner.grant.set", map[string]any{"scope": f.scope, "expectedRevision": Revision(1), "active": false}, uuid.NewString())
	if reply.Error != nil {
		t.Fatal(reply.Error.Code)
	}
	replyCode(t, f.call(t, f.visitorHTTP, "lease.acquire", parameters, ""), "AUTH_REQUIRED")
	// An old successful operation reports its historical result without reapplying it.
	replyCode(t, f.call(t, f.publisherHTTP, "lease.validate", map[string]any{"token": lease.Token}, ""), "AUTH_REQUIRED")
	reply = f.call(t, f.ownerHTTP, "owner.grant.set", map[string]any{"scope": f.scope, "expectedRevision": Revision(0), "active": true}, operation)
	if reply.Error != nil {
		t.Fatal(reply.Error.Code)
	}
	replyCode(t, f.call(t, f.visitorHTTP, "lease.acquire", parameters, ""), "AUTH_REQUIRED")
	status := f.call(t, f.ownerHTTP, "owner.operation.status", map[string]any{"operationId": operation}, "")
	if status.Error != nil {
		t.Fatal(status.Error.Code)
	}
	var historical Outcome
	if decode(status.Result, &historical) != nil || !historical.Active || historical.Revision != 1 {
		t.Fatal("original operation not retained")
	}
	// Keep-alive TLS cannot bypass a subsequent device revocation.
	reply = f.call(t, f.ownerHTTP, "owner.device.set", map[string]any{"device": f.visitor.ID, "expectedRevision": Revision(1), "roles": Roles{}, "active": false}, uuid.NewString())
	if reply.Error != nil {
		t.Fatal(reply.Error.Code)
	}
	replyCode(t, f.call(t, f.visitorHTTP, "hello", map[string]any{}, ""), "AUTH_REQUIRED")
}
func TestControlWireRejectsNullAndUnknownWithoutMutation(t *testing.T) {
	f := newHTTPFixture(t)
	before, err := f.store.witness.Read()
	if err != nil {
		t.Fatal(err)
	}
	id := uuid.NewString()
	operation := uuid.NewString()
	for _, body := range []string{
		`{"kind":"request","apiVersion":1,"apiVersion":1,"requestId":"` + id + `","method":"hello","params":{}}`,
		`{"kind":"request","apiVersion":1,"requestId":"` + id + `","method":"hello","params":null}`,
		`{"kind":"request","apiVersion":1,"requestId":"` + id + `","method":"owner.device.set","operationId":"` + operation + `","params":{"device":"` + f.visitor.ID + `","expectedRevision":"1","roles":{"owner":false,"publish":false,"access":false},"active":null}}`,
		strings.Repeat(" ", 65537),
	} {
		response, err := f.ownerHTTP.Post(f.url, "application/json", strings.NewReader(body))
		if err != nil {
			t.Fatal(err)
		}
		payload, _ := io.ReadAll(response.Body)
		response.Body.Close()
		if response.StatusCode != 400 || strings.Contains(string(payload), f.visitor.ID) || !strings.Contains(string(payload), "INVALID_REQUEST") {
			t.Fatal("malformed control input was not rejected safely")
		}
	}
	after, err := f.store.witness.Read()
	if err != nil || before.Committed != after.Committed || after.Prepared != nil {
		t.Fatal("invalid control requests changed authority")
	}
	replyCode(t, f.call(t, f.ownerHTTP, "owner.device.set", map[string]any{"device": f.visitor.ID, "expectedRevision": 1, "roles": Roles{}, "active": false}, uuid.NewString()), "INVALID_REQUEST")
	replyCode(t, f.call(t, f.ownerHTTP, "owner.device.set", map[string]any{"device": f.visitor.ID, "expectedRevision": Revision(1), "roles": map[string]any{}, "active": false}, uuid.NewString()), "INVALID_REQUEST")
}
func TestControlAdmissionPerDeviceAndGlobalBounds(t *testing.T) {
	server := &ControlServer{active: make(map[string]int)}
	for device := 0; device < 4; device++ {
		for call := 0; call < 8; call++ {
			if !server.acquire(string(rune('a' + device))) {
				t.Fatal("allowed admission refused")
			}
		}
	}
	if server.acquire("a") || server.acquire("next") {
		t.Fatal("control capacity exceeded")
	}
	server.release("a")
	if !server.acquire("next") {
		t.Fatal("released slot unavailable")
	}
	server.release("next")
	for device := 0; device < 4; device++ {
		limit := 8
		if device == 0 {
			limit = 7
		}
		for call := 0; call < limit; call++ {
			server.release(string(rune('a' + device)))
		}
	}
	if server.total != 0 || len(server.active) != 0 {
		t.Fatal("control caller quota leaked")
	}
}

func TestCurrentAuthorityBatchActualActorAndRevocation(t *testing.T) {
	f := newHTTPFixture(t)
	f.grant(t)
	value := map[string]any{"scope": f.scope, "visitorRevision": Revision(1), "publisherRevision": Revision(1), "serviceRevision": Revision(1), "grantRevision": Revision(1), "protocol": "https", "authority": "files.example.invalid"}
	check := func(client *http.Client, expected bool) {
		t.Helper()
		reply := f.call(t, client, "lease.check", map[string]any{"leases": []any{value}}, "")
		var result struct {
			Valid       []bool `json:"valid"`
			ServerEpoch string `json:"serverEpoch"`
			Issuer      string `json:"issuer"`
		}
		if reply.Error != nil || decode(reply.Result, &result) != nil || len(result.Valid) != 1 || result.Valid[0] != expected || result.ServerEpoch != f.server.issuer.Epoch || result.Issuer != f.server.identity.ID {
			t.Fatal("actual current authority check mismatch")
		}
	}
	check(f.visitorHTTP, true)
	check(f.publisherHTTP, true)
	replyCode(t, f.call(t, f.ownerHTTP, "lease.check", map[string]any{"leases": []any{value}}, ""), "AUTH_REQUIRED")
	delete(value, "authority")
	replyCode(t, f.call(t, f.publisherHTTP, "lease.check", map[string]any{"leases": []any{value}}, ""), "INVALID_REQUEST")
	value["authority"] = "files.example.invalid"
	many := make([]any, 64)
	for i := range many {
		many[i] = value
	}
	if reply := f.call(t, f.publisherHTTP, "lease.check", map[string]any{"leases": many}, ""); reply.Error != nil {
		t.Fatal("bounded batch rejected")
	}
	many = append(many, value)
	replyCode(t, f.call(t, f.publisherHTTP, "lease.check", map[string]any{"leases": many}, ""), "INVALID_REQUEST")
	if _, err := f.store.SetGrant(f.owner.ID, uuid.NewString(), f.scope, 1, false, time.Now()); err != nil {
		t.Fatal(err)
	}
	check(f.visitorHTTP, false)
	check(f.publisherHTTP, false)
}
