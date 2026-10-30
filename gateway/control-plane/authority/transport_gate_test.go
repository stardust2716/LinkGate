package authority

import (
	"context"
	"crypto/tls"
	"encoding/binary"
	"net"
	"testing"
	"time"

	"github.com/google/uuid"
)

type transportFixture struct {
	base *httpFixture
	gate *TransportGate
}

func newTransportFixture(t *testing.T) *transportFixture {
	t.Helper()
	base := newHTTPFixture(t)
	base.grant(t)
	gate, err := NewTransportGate(base.server.identity, base.store, time.Now)
	if err != nil {
		t.Fatal("transport authority setup failed")
	}
	t.Cleanup(gate.Close)
	return &transportFixture{base, gate}
}
func (f *transportFixture) connect(t *testing.T, device *DeviceIdentity, application, role string) (*TransportPeer, error) {
	t.Helper()
	raw, _ := encode(agentScope{"linkgate.bridge", 1, application, role})
	return f.connectRaw(t, device, raw)
}
func (f *transportFixture) connectRaw(t *testing.T, device *DeviceIdentity, preface []byte) (*TransportPeer, error) {
	t.Helper()
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal("transport listener setup failed")
	}
	defer listener.Close()
	type accepted struct {
		connection *TransportConn
		err        error
	}
	ready := make(chan accepted, 1)
	go func() {
		raw, e := listener.Accept()
		if e != nil {
			ready <- accepted{nil, e}
			return
		}
		connection, e := f.gate.Accept(context.Background(), raw)
		ready <- accepted{connection, e}
	}()
	configuration, err := device.ClientTLS(f.base.server.identity.ID, time.Now)
	if err != nil {
		t.Fatal("transport identity setup failed")
	}
	configuration.NextProtos = []string{TransportALPN}
	client, err := tls.DialWithDialer(&net.Dialer{Timeout: time.Second}, "tcp4", listener.Addr().String(), configuration)
	if err == nil {
		t.Cleanup(func() { _ = client.Close() })
		_ = client.SetDeadline(time.Now().Add(time.Second))
		var header [4]byte
		binary.LittleEndian.PutUint32(header[:], uint32(len(preface)))
		_, _ = client.Write(header[:])
		_, _ = client.Write(preface)
		_ = client.SetDeadline(time.Time{})
	}
	select {
	case result := <-ready:
		if result.err != nil {
			return nil, result.err
		}
		t.Cleanup(func() { _ = result.connection.Close() })
		return result.connection.Peer(), nil
	case <-time.After(5 * time.Second):
		t.Fatal("transport authentication did not finish")
	}
	return nil, reject("AUTH_REQUIRED")
}
func (f *transportFixture) control(t *testing.T, device *DeviceIdentity, application, role, run string) *TransportControl {
	t.Helper()
	peer, err := f.connect(t, device, application, role)
	if err != nil {
		t.Fatal("actual transport peer rejected")
	}
	control, err := f.gate.Login(peer, run, peer.Namespace())
	if err != nil {
		t.Fatal("actual transport control rejected")
	}
	return control
}
func waitTransportClosed(t *testing.T, done <-chan struct{}) {
	t.Helper()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("transport authority did not close within budget")
	}
}
func TestActualTransportRoleScopeAndRunBinding(t *testing.T) {
	f := newTransportFixture(t)
	publisher := f.control(t, f.base.publisher, f.base.scope.Target.PublisherApp, "publish", "publisher-01")
	visitor := f.control(t, f.base.visitor, f.base.scope.VisitorApp, "access", "visitor-01")
	name := TransportProxyName(f.base.scope.Target, "stcp")
	publication, err := f.gate.Publish(publisher, f.base.scope.Target.Service, "stcp", name)
	if err != nil {
		t.Fatal("approved publication rejected")
	}
	if _, err = f.gate.Publish(visitor, f.base.scope.Target.Service, "stcp", name); err == nil {
		t.Fatal("access role published a service")
	}
	if _, err = f.gate.Publish(publisher, "unapproved", "stcp", name); err == nil {
		t.Fatal("unapproved publication accepted")
	}
	if _, err = f.gate.Publish(publisher, f.base.scope.Target.Service, "tcp", name); err == nil {
		t.Fatal("public TCP proxy accepted")
	}
	authorization, err := f.gate.Visit(visitor, name, "stcp")
	if err != nil || !authorization.Alive() {
		t.Fatal("approved visitor rejected")
	}
	if authorization.PublisherNamespace() != publisher.Namespace() || authorization.VisitorNamespace() != visitor.Namespace() {
		t.Fatal("visitor identity binding lost")
	}
	if _, err = f.gate.Visit(visitor, name, "xtcp"); err == nil {
		t.Fatal("relay proxy borrowed as hole-punch target")
	}
	work, err := f.connect(t, f.base.publisher, f.base.scope.Target.PublisherApp, "publish")
	if err != nil {
		t.Fatal("work proof rejected")
	}
	if _, err = f.gate.Bind(work, ""); err == nil {
		t.Fatal("empty work RunID accepted")
	}
	bound, err := f.gate.Bind(work, "publisher-01")
	if err != nil || bound != publisher {
		t.Fatal("work connection lost its actual control subject")
	}
	if again, err := f.gate.Bind(work, "publisher-01"); err != nil || again != publisher {
		t.Fatal("multiplexed proof lost its same control binding")
	}
	otherApp, err := f.connect(t, f.base.publisher, "org.example.other", "publish")
	if err != nil {
		t.Fatal("application proof setup failed")
	}
	if _, err = f.gate.Bind(otherApp, "publisher-01"); err == nil {
		t.Fatal("different application borrowed a control")
	}
	otherRole, err := f.connect(t, f.base.visitor, f.base.scope.Target.PublisherApp, "access")
	if err != nil {
		t.Fatal("visitor proof setup failed")
	}
	if _, err = f.gate.Bind(otherRole, "publisher-01"); err == nil {
		t.Fatal("different device/role borrowed a control")
	}
	if _, err = f.gate.Login(otherApp, "publisher-01", otherApp.Namespace()); err == nil {
		t.Fatal("active RunID hijacked")
	}
	if _, err = f.gate.Login(otherApp, "other-app-01", publisher.Namespace()); err == nil {
		t.Fatal("claimed user replaced actual namespace")
	}
	if _, err = f.gate.Login(otherApp, "", otherApp.Namespace()); err == nil {
		t.Fatal("empty login RunID accepted")
	}
	publication.Close()
	waitTransportClosed(t, authorization.Done())
}
func TestActualTransportPrefaceAndRoleFailureClosed(t *testing.T) {
	f := newTransportFixture(t)
	if _, err := f.connect(t, f.base.visitor, f.base.scope.VisitorApp, "publish"); err == nil {
		t.Fatal("unapproved publish role admitted")
	}
	for _, value := range []string{`{"kind":"linkgate.bridge","apiVersion":1,"applicationId":"org.example.client","role":"access","role":"publish"}`, `{"kind":"linkgate.bridge","apiVersion":1,"applicationId":"org.example.client","role":"access","device":"self-asserted"}`, `{"kind":"linkgate.bridge","apiVersion":1,"applicationId":"org.example.\ud800","role":"access"}`} {
		if _, err := f.connectRaw(t, f.base.visitor, []byte(value)); err == nil {
			t.Fatal("ambiguous Agent scope accepted")
		}
	}
	peer, err := f.connect(t, f.base.publisher, f.base.scope.Target.PublisherApp, "publish")
	if err != nil {
		t.Fatal("authenticated peer setup failed")
	}
	foreign, err := NewTransportGate(f.base.server.identity, f.base.store, time.Now)
	if err != nil {
		t.Fatal("foreign gate setup failed")
	}
	defer foreign.Close()
	if _, err = foreign.Login(peer, "foreign-01", peer.Namespace()); err == nil {
		t.Fatal("peer proof crossed gate instance")
	}
}
func TestTransportCurrentGrantAndPublicationRevocation(t *testing.T) {
	f := newTransportFixture(t)
	publisher := f.control(t, f.base.publisher, f.base.scope.Target.PublisherApp, "publish", "publisher-01")
	visitor := f.control(t, f.base.visitor, f.base.scope.VisitorApp, "access", "visitor-01")
	name := TransportProxyName(f.base.scope.Target, "xtcp")
	publication, err := f.gate.Publish(publisher, f.base.scope.Target.Service, "xtcp", name)
	if err != nil {
		t.Fatal("publication rejected")
	}
	a, err := f.gate.Visit(visitor, name, "xtcp")
	if err != nil {
		t.Fatal("hole-punch authorization rejected")
	}
	if _, err = f.base.store.SetGrant(f.base.owner.ID, uuid.NewString(), f.base.scope, 1, false, time.Now()); err != nil {
		t.Fatal("grant revoke failed")
	}
	waitTransportClosed(t, a.Done())
	if _, err = f.base.store.SetGrant(f.base.owner.ID, uuid.NewString(), f.base.scope, 2, true, time.Now()); err != nil {
		t.Fatal("grant reapproval failed")
	}
	if f.gate.Renew(a) == nil {
		t.Fatal("closed authority revived after reapproval")
	}
	if _, err = f.gate.Visit(visitor, name, "xtcp"); err != nil {
		t.Fatal("new operation after explicit reapproval rejected")
	}
	if _, err = f.base.store.SetService(f.base.owner.ID, uuid.NewString(), f.base.scope.Target, 1, "https", "files.example.invalid", true, time.Now()); err != nil {
		t.Fatal("publication generation change failed")
	}
	waitTransportClosed(t, publication.Done())
	if f.gate.Publisher(publisher, name) == nil {
		t.Fatal("old publication survived generation change")
	}
}
func TestTransportExpiryIndependentOfBlockedAuthorityAndControlHeartbeat(t *testing.T) {
	f := newTransportFixture(t)
	publisher := f.control(t, f.base.publisher, f.base.scope.Target.PublisherApp, "publish", "publisher-01")
	visitor := f.control(t, f.base.visitor, f.base.scope.VisitorApp, "access", "visitor-01")
	name := TransportProxyName(f.base.scope.Target, "stcp")
	if _, err := f.gate.Publish(publisher, f.base.scope.Target.Service, "stcp", name); err != nil {
		t.Fatal("publication rejected")
	}
	a, err := f.gate.Visit(visitor, name, "stcp")
	if err != nil {
		t.Fatal("visitor rejected")
	}
	// Shorten the test-owned deadline; do not accelerate protocol wall clocks or
	// claim that this is the 120-second partition or long-duration acceptance.
	a.mu.Lock()
	a.expires = time.Now().Add(150 * time.Millisecond)
	a.mu.Unlock()
	f.base.store.mu.Lock()
	waitTransportClosed(t, a.Done())
	f.base.store.mu.Unlock()
	publisher.mu.Lock()
	publisher.heartbeat = time.Now().Add(-4 * time.Second)
	publisher.mu.Unlock()
	waitTransportClosed(t, publisher.Done())
	if f.gate.Heartbeat(publisher) == nil {
		t.Fatal("closed control revived by late heartbeat")
	}
}

func TestTransportWaitingVisitorsCannotStarveControlLane(t *testing.T) {
	f := newTransportFixture(t)
	control := f.control(t, f.base.visitor, f.base.scope.VisitorApp, "access", "visitor-01")
	var release []func()
	for i := 0; i < 8; i++ {
		done, err := f.gate.Request(control, true)
		if err != nil {
			t.Fatal("bounded visitor lane rejected")
		}
		release = append(release, done)
	}
	if _, err := f.gate.Request(control, true); Code(err) != "RESOURCE_EXHAUSTED" {
		t.Fatal("visitor lane exceeded bound")
	}
	reply, err := f.gate.Request(control, false)
	if err != nil {
		t.Fatal("pending visitors starved their control lane")
	}
	reply()
	reply()
	for _, done := range release {
		done()
	}
}
func TestTransportStopClosesSocketsBeforeBlockedStorageDrains(t *testing.T) {
	f := newTransportFixture(t)
	control := f.control(t, f.base.publisher, f.base.scope.Target.PublisherApp, "publish", "publisher-01")
	f.base.store.mu.Lock()
	// Ensure the current-authority worker has entered its independently bounded
	// storage lane while the expiry/stop path remains available.
	time.Sleep(600 * time.Millisecond)
	before := time.Now()
	f.gate.Close()
	if time.Since(before) > time.Second {
		f.base.store.mu.Unlock()
		t.Fatal("blocked storage hung network shutdown")
	}
	waitTransportClosed(t, control.Done())
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
	err := f.gate.WaitClosed(ctx)
	cancel()
	f.base.store.mu.Unlock()
	if err == nil {
		t.Fatal("blocked worker incorrectly reported drained")
	}
	ctx, cancel = context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	if f.gate.WaitClosed(ctx) != nil {
		t.Fatal("released worker did not finish")
	}
}
