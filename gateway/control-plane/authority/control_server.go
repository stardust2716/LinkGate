package authority

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/tls"
	"encoding/base64"
	"encoding/json/jsontext"
	"io"
	"log"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"
)

type controlRequest struct {
	Kind        string         `json:"kind"`
	APIVersion  int            `json:"apiVersion"`
	RequestID   string         `json:"requestId"`
	Method      string         `json:"method"`
	Params      jsontext.Value `json:"params"`
	OperationID string         `json:"operationId,omitempty"`
}
type controlReply struct {
	Kind       string        `json:"kind"`
	APIVersion int           `json:"apiVersion"`
	RequestID  string        `json:"requestId"`
	Result     any           `json:"result,omitempty"`
	Error      *controlError `json:"error,omitempty"`
}
type controlError struct {
	Code      string `json:"code"`
	Stage     string `json:"stage"`
	Retryable bool   `json:"retryable"`
}
type leaseResult struct {
	Current     Snapshot `json:"current"`
	Token       string   `json:"token"`
	ServerEpoch string   `json:"serverEpoch"`
	Issuer      string   `json:"issuer"`
}
type validationResult struct {
	Current     Snapshot `json:"current"`
	ServerEpoch string   `json:"serverEpoch"`
	Issuer      string   `json:"issuer"`
}

type ControlServer struct {
	statistics controlCounters
	identity   *DeviceIdentity
	store      *Store
	issuer     *LeaseIssuer
	clock      func() time.Time
	tls        *tls.Config
	http       *http.Server
	mu         sync.Mutex
	active     map[string]int
	total      int
}

func NewControlServer(identity *DeviceIdentity, store *Store, clock func() time.Time) (*ControlServer, error) {
	if identity == nil || identity.Root == nil || identity.Leaf == nil || store == nil || clock == nil || identity.ID != store.server || hash(identity.Root.Raw) != store.root || validateDeviceProfile(identity.Leaf, identity.Root, clock()) != nil {
		return nil, reject("IDENTITY_CHANGED")
	}
	key, ok := identity.Certificate.PrivateKey.(*ecdsa.PrivateKey)
	if !ok {
		return nil, reject("IDENTITY_CHANGED")
	}
	issuer, err := NewLeaseIssuer(key)
	if err != nil || issuer.ID != identity.ID {
		return nil, reject("IDENTITY_CHANGED")
	}
	server := &ControlServer{identity: identity, store: store, issuer: issuer, clock: clock, active: make(map[string]int)}
	server.tls, err = identity.ServerTLS(func(device string) bool { _, err := store.AdmitDevice(device, "paired", clock()); return err == nil }, clock)
	if err != nil {
		return nil, err
	}
	server.tls.NextProtos = []string{"http/1.1"}
	server.http = &http.Server{Handler: server, ReadHeaderTimeout: 2 * time.Second, ReadTimeout: 3 * time.Second, WriteTimeout: 3 * time.Second, IdleTimeout: 5 * time.Second, MaxHeaderBytes: 16384, ErrorLog: log.New(io.Discard, "", 0), TLSNextProto: map[string]func(*http.Server, *tls.Conn, http.Handler){}}
	return server, nil
}
func (s *ControlServer) Serve(listener net.Listener) error {
	if listener == nil {
		return reject("INVALID_REQUEST")
	}
	bounded := &controlListener{Listener: listener, slots: make(chan struct{}, 128)}
	return s.http.Serve(tls.NewListener(bounded, s.tls))
}
func (s *ControlServer) Close() error { return s.http.Close() }
func (s *ControlServer) acquire(device string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.total >= 32 || s.active[device] >= 8 {
		return false
	}
	s.total++
	s.active[device]++
	return true
}
func (s *ControlServer) release(device string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.total--
	s.active[device]--
	if s.active[device] == 0 {
		delete(s.active, device)
	}
}

func exact(input []byte, value any, fields ...string) error {
	var members map[string]jsontext.Value
	if decode(input, &members) != nil || len(members) != len(fields) {
		return reject("INVALID_REQUEST")
	}
	for _, field := range fields {
		if _, exists := members[field]; !exists {
			return reject("INVALID_REQUEST")
		}
	}
	return decode(input, value)
}
func noNull(input []byte) bool {
	decoder := jsontext.NewDecoder(bytes.NewReader(input))
	for {
		token, err := decoder.ReadToken()
		if err == io.EOF {
			return true
		}
		if err != nil || token.Kind() == 'n' {
			return false
		}
	}
}
func (s *ControlServer) ServeHTTP(writer http.ResponseWriter, request *http.Request) {
	writer.Header().Set("Content-Type", "application/json")
	writer.Header().Set("Cache-Control", "no-store")
	uncorrelated := func(status int) {
		writer.WriteHeader(status)
		_, _ = writer.Write([]byte(`{"error":{"code":"INVALID_REQUEST"}}`))
	}
	if request.Method != http.MethodPost || request.URL.Path != "/control/v1" || request.URL.RawQuery != "" || request.Header.Get("Content-Type") != "application/json" || request.TLS == nil {
		uncorrelated(http.StatusBadRequest)
		return
	}
	device, err := HandshakeDevice(*request.TLS)
	if err != nil {
		uncorrelated(http.StatusUnauthorized)
		return
	}
	input, err := io.ReadAll(io.LimitReader(request.Body, 65537))
	if err != nil || len(input) > 65536 {
		uncorrelated(http.StatusBadRequest)
		return
	}
	var frame controlRequest
	if decode(input, &frame) != nil || !noNull(input) || frame.Kind != "request" || !validUUID(frame.RequestID) || len(frame.Method) == 0 || len(frame.Method) > 64 {
		uncorrelated(http.StatusBadRequest)
		return
	}
	fields := []string{"kind", "apiVersion", "requestId", "method", "params"}
	mutating := frame.Method == "owner.device.set" || frame.Method == "owner.service.set" || frame.Method == "owner.grant.set"
	if mutating {
		fields = append(fields, "operationId")
	}
	if exact(input, &frame, fields...) != nil || (mutating && !validUUID(frame.OperationID)) {
		s.reply(writer, frame.RequestID, nil, reject("INVALID_REQUEST"))
		return
	}
	if frame.APIVersion != 1 {
		s.reply(writer, frame.RequestID, nil, reject("VERSION_UNSUPPORTED"))
		return
	}
	if !s.acquire(device) {
		s.reply(writer, frame.RequestID, nil, reject("RESOURCE_EXHAUSTED"))
		return
	}
	defer s.release(device)
	now := s.clock()
	if validateDeviceProfile(s.identity.Leaf, s.identity.Root, now) != nil {
		s.reply(writer, frame.RequestID, nil, reject("IDENTITY_CHANGED"))
		return
	}
	if _, err = s.store.AdmitDevice(device, "paired", now); err != nil {
		s.reply(writer, frame.RequestID, nil, err)
		return
	}
	result, err := s.dispatch(device, frame, now)
	s.reply(writer, frame.RequestID, result, err)
}
func (s *ControlServer) reply(writer http.ResponseWriter, id string, result any, err error) {
	value := controlReply{Kind: "response", APIVersion: 1, RequestID: id, Result: result}
	if err != nil {
		value.Result = nil
		value.Error = &controlError{Code: Code(err), Stage: "control", Retryable: false}
	}
	encoded, err := encode(value)
	if err != nil {
		writer.WriteHeader(http.StatusInternalServerError)
		_, _ = writer.Write([]byte(`{"error":{"code":"STORAGE_UNAVAILABLE"}}`))
		return
	}
	_, _ = writer.Write(encoded)
}
func (s *ControlServer) dispatch(device string, frame controlRequest, now time.Time) (any, error) {
	if frame.Method == "hello" {
		if exact(frame.Params, &struct{}{}) != nil {
			return nil, reject("INVALID_REQUEST")
		}
		return struct {
			Server          string `json:"server"`
			Epoch           string `json:"epoch"`
			MaxLeaseSeconds int    `json:"maxLeaseSeconds"`
		}{s.identity.ID, s.issuer.Epoch, 120}, nil
	}
	if frame.Method == "lease.acquire" {
		if _, err := s.store.AdmitDevice(device, "access", now); err != nil {
			return nil, err
		}
		var parameters struct {
			VisitorApp string       `json:"visitorApp"`
			Target     ServiceScope `json:"target"`
		}
		if exact(frame.Params, &parameters, "visitorApp", "target") != nil {
			return nil, reject("INVALID_REQUEST")
		}
		scope := AccessScope{VisitorDevice: device, VisitorApp: parameters.VisitorApp, Target: parameters.Target}
		snapshot, err := s.store.Snapshot(scope, now)
		if err != nil {
			return nil, err
		}
		lifetime := 120 * time.Second
		for _, expiry := range []time.Time{s.identity.Leaf.NotAfter, s.identity.Root.NotAfter} {
			if expiry.Sub(now) < lifetime {
				lifetime = expiry.Sub(now)
			}
		}
		lifetime = lifetime.Truncate(time.Second)
		if lifetime < time.Second {
			return nil, reject("AUTH_REQUIRED")
		}
		token, err := s.issuer.Issue(snapshot, now, lifetime)
		if err != nil {
			return nil, err
		}
		s.statistics.issued.record()
		return leaseResult{Current: snapshot, Token: token, ServerEpoch: s.issuer.Epoch, Issuer: s.issuer.ID}, nil
	}
	if frame.Method == "lease.validate" {
		if _, err := s.store.AdmitDevice(device, "publish", now); err != nil {
			return nil, err
		}
		var parameters struct {
			Token string `json:"token"`
		}
		if exact(frame.Params, &parameters, "token") != nil || len(parameters.Token) > 16384 {
			return nil, reject("INVALID_REQUEST")
		}
		// This unverified scope is used only for an authenticated, publisher-bound
		// lookup. It becomes authority only after VerifyLease succeeds below.
		parts := strings.Split(parameters.Token, ".")
		if len(parts) != 3 {
			return nil, reject("AUTH_REQUIRED")
		}
		payload, err := base64.RawURLEncoding.Strict().DecodeString(parts[1])
		if err != nil {
			return nil, reject("AUTH_REQUIRED")
		}
		var claimed leaseClaims
		if decode(payload, &claimed) != nil || claimed.Scope.Target.PublisherDevice != device {
			return nil, reject("AUTH_REQUIRED")
		}
		current, err := s.store.Snapshot(claimed.Scope, now)
		if err != nil {
			return nil, err
		}
		key := s.identity.Leaf.PublicKey.(*ecdsa.PublicKey)
		if _, err = VerifyLease(parameters.Token, key, s.identity.ID, s.issuer.Epoch, current, now, 0); err != nil {
			return nil, err
		}
		s.statistics.validated.record()
		return validationResult{Current: current, ServerEpoch: s.issuer.Epoch, Issuer: s.identity.ID}, nil
	}
	if frame.Method == "lease.check" {
		var parameters struct {
			Leases []struct {
				Scope     AccessScope `json:"scope"`
				Visitor   Revision    `json:"visitorRevision"`
				Publisher Revision    `json:"publisherRevision"`
				Service   Revision    `json:"serviceRevision"`
				Grant     Revision    `json:"grantRevision"`
				Protocol  string      `json:"protocol"`
				Authority string      `json:"authority"`
			} `json:"leases"`
		}
		if exact(frame.Params, &parameters, "leases") != nil || len(parameters.Leases) < 1 || len(parameters.Leases) > 64 {
			return nil, reject("INVALID_REQUEST")
		}
		var raw struct {
			Leases []jsontext.Value `json:"leases"`
		}
		if decode(frame.Params, &raw) != nil {
			return nil, reject("INVALID_REQUEST")
		}
		valid := make([]bool, len(parameters.Leases))
		for index, wanted := range parameters.Leases {
			if exact(raw.Leases[index], &wanted, "scope", "visitorRevision", "publisherRevision", "serviceRevision", "grantRevision", "protocol", "authority") != nil {
				return nil, reject("INVALID_REQUEST")
			}
			if !wanted.Scope.Valid() || wanted.Visitor == 0 || wanted.Publisher == 0 || wanted.Service == 0 || wanted.Grant == 0 || !validProtocol(wanted.Protocol, wanted.Authority) {
				return nil, reject("INVALID_REQUEST")
			}
			role := ""
			if wanted.Scope.Target.PublisherDevice == device {
				role = "publish"
			} else if wanted.Scope.VisitorDevice == device {
				role = "access"
			} else {
				return nil, reject("AUTH_REQUIRED")
			}
			if _, err := s.store.AdmitDevice(device, role, now); err != nil {
				return nil, err
			}
			current, err := s.store.Snapshot(wanted.Scope, now)
			if err != nil {
				if Code(err) == "AUTH_REQUIRED" {
					continue
				}
				return nil, err
			}
			g, service := current.Grant, current.Service
			valid[index] = wanted.Visitor == g.VisitorRevision && wanted.Publisher == g.PublisherRevision && wanted.Service == service.Revision && wanted.Grant == g.Revision && wanted.Protocol == service.Protocol && wanted.Authority == service.Authority
		}
		s.statistics.checked.record()
		return struct {
			Valid       []bool `json:"valid"`
			ServerEpoch string `json:"serverEpoch"`
			Issuer      string `json:"issuer"`
		}{valid, s.issuer.Epoch, s.identity.ID}, nil
	}
	if frame.Method == "owner.operation.status" {
		if _, err := s.store.AdmitDevice(device, "owner", now); err != nil {
			return nil, err
		}
		var parameters struct {
			Operation string `json:"operationId"`
		}
		if exact(frame.Params, &parameters, "operationId") != nil {
			return nil, reject("INVALID_REQUEST")
		}
		return s.store.OwnerOperation(device, parameters.Operation, now)
	}
	if frame.Method == "owner.device.set" || frame.Method == "owner.service.set" || frame.Method == "owner.grant.set" {
		if _, err := s.store.AdmitDevice(device, "owner", now); err != nil {
			return nil, err
		}
		switch frame.Method {
		case "owner.device.set":
			var parameters struct {
				Device   string   `json:"device"`
				Expected Revision `json:"expectedRevision"`
				Roles    Roles    `json:"roles"`
				Active   bool     `json:"active"`
			}
			if exact(frame.Params, &parameters, "device", "expectedRevision", "roles", "active") != nil {
				return nil, reject("INVALID_REQUEST")
			}
			var raw map[string]jsontext.Value
			if decode(frame.Params, &raw) != nil || exact(raw["roles"], &parameters.Roles, "owner", "publish", "access") != nil {
				return nil, reject("INVALID_REQUEST")
			}
			return s.store.SetDevice(device, frame.OperationID, parameters.Device, parameters.Expected, parameters.Roles, parameters.Active, now)
		case "owner.service.set":
			var parameters struct {
				Scope     ServiceScope `json:"scope"`
				Expected  Revision     `json:"expectedRevision"`
				Protocol  string       `json:"protocol"`
				Authority string       `json:"authority"`
				Active    bool         `json:"active"`
			}
			if exact(frame.Params, &parameters, "scope", "expectedRevision", "protocol", "authority", "active") != nil {
				return nil, reject("INVALID_REQUEST")
			}
			return s.store.SetService(device, frame.OperationID, parameters.Scope, parameters.Expected, parameters.Protocol, parameters.Authority, parameters.Active, now)
		case "owner.grant.set":
			var parameters struct {
				Scope    AccessScope `json:"scope"`
				Expected Revision    `json:"expectedRevision"`
				Active   bool        `json:"active"`
			}
			if exact(frame.Params, &parameters, "scope", "expectedRevision", "active") != nil {
				return nil, reject("INVALID_REQUEST")
			}
			return s.store.SetGrant(device, frame.OperationID, parameters.Scope, parameters.Expected, parameters.Active, now)
		}
	}
	return nil, reject("METHOD_UNSUPPORTED")
}

type controlListener struct {
	net.Listener
	slots chan struct{}
}

func (l *controlListener) Accept() (net.Conn, error) {
	for {
		connection, err := l.Listener.Accept()
		if err != nil {
			return nil, err
		}
		select {
		case l.slots <- struct{}{}:
			return &controlConn{Conn: connection, release: func() { <-l.slots }}, nil
		default:
			connection.Close()
		}
	}
}

type controlConn struct {
	net.Conn
	once    sync.Once
	release func()
}

func (c *controlConn) Close() error { err := c.Conn.Close(); c.once.Do(c.release); return err }
