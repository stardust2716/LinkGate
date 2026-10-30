package server

import (
	"bytes"
	"context"
	"io"
	"net"
	"sync"
	"testing"
	"time"

	"github.com/fatedier/frp/internal/linkgatetest"
	v1 "github.com/fatedier/frp/pkg/config/v1"
	"github.com/fatedier/frp/pkg/msg"
	"github.com/fatedier/frp/pkg/util/log"
	netpkg "github.com/fatedier/frp/pkg/util/net"
	"github.com/fatedier/frp/pkg/util/util"
	"github.com/google/uuid"
	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
)

func startLinkGateService(t *testing.T) (*linkgatetest.Fixture, *Service) {
	t.Helper()
	log.InitLogger("console", "error", 1, true)
	f := linkgatetest.New(t)
	cfg := &v1.ServerConfig{}
	if cfg.Complete() != nil {
		t.Fatal("backend fixture configuration failed")
	}
	cfg.BindAddr = "127.0.0.1"
	cfg.BindPort = 0
	cfg.ProxyBindAddr = "127.0.0.1"
	enabled := false
	cfg.Transport.TCPMux = &enabled
	cfg.Transport.HeartbeatTimeout = 3
	s, err := NewAuthenticatedService(cfg, f.Gate)
	if err != nil {
		t.Fatal("authenticated backend construction failed")
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() { defer close(done); s.Run(ctx) }()
	t.Cleanup(func() {
		cancel()
		_ = s.Close()
		select {
		case <-done:
		case <-time.After(5 * time.Second):
			t.Error("authenticated backend did not stop")
		}
	})
	return f, s
}

type linkgateControlClient struct {
	conn      net.Conn
	rw        io.ReadWriter
	mu        sync.Mutex
	run, user string
	proxies   chan *msg.NewProxyResp
	work      chan struct{}
	nat       chan *msg.NatHoleResp
	done      chan struct{}
}

func newLinkGateControl(t *testing.T, f *linkgatetest.Fixture, s *Service, device *authority.DeviceIdentity, application, role, run string) *linkgateControlClient {
	return newLinkGateInstance(t, f, s, device, application, role, run, "")
}
func newLinkGateInstance(t *testing.T, f *linkgatetest.Fixture, s *Service, device *authority.DeviceIdentity, application, role, run, suffix string) *linkgateControlClient {
	t.Helper()
	raw := f.Dial(t, s.listener.Addr().String(), device, application, role)
	namespace := authority.TransportNamespace(device.ID, application, role)
	if msg.WriteMsg(raw, &msg.Login{RunID: run, User: namespace, ClientID: namespace + suffix, Timestamp: time.Now().Unix()}) != nil {
		t.Fatal("actual backend login write failed")
	}
	_ = raw.SetReadDeadline(time.Now().Add(3 * time.Second))
	reply, err := msg.ReadMsg(raw)
	if err != nil {
		t.Fatal("actual backend login response failed")
	}
	_ = raw.SetReadDeadline(time.Time{})
	login, ok := reply.(*msg.LoginResp)
	if !ok || login.Error != "" || login.RunID != run {
		t.Fatal("actual authenticated backend login rejected")
	}
	rw, err := netpkg.NewCryptoReadWriter(raw, nil)
	if err != nil {
		t.Fatal("backend inner control codec failed")
	}
	c := &linkgateControlClient{conn: raw, rw: rw, run: run, user: namespace, proxies: make(chan *msg.NewProxyResp, 16), work: make(chan struct{}, 16), nat: make(chan *msg.NatHoleResp, 16), done: make(chan struct{})}
	go func() {
		defer close(c.done)
		for {
			m, e := msg.ReadMsg(c.rw)
			if e != nil {
				return
			}
			switch value := m.(type) {
			case *msg.NewProxyResp:
				select {
				case c.proxies <- value:
				default:
					return
				}
			case *msg.ReqWorkConn:
				select {
				case c.work <- struct{}{}:
				default:
					return
				}
			case *msg.NatHoleResp:
				select {
				case c.nat <- value:
				default:
					return
				}
			case *msg.Pong:
				if value.Error != "" {
					return
				}
			}
		}
	}()
	stop := make(chan struct{})
	go func() {
		timer := time.NewTicker(500 * time.Millisecond)
		defer timer.Stop()
		for {
			select {
			case <-stop:
				return
			case <-c.done:
				return
			case <-timer.C:
				if c.send(&msg.Ping{}) != nil {
					return
				}
			}
		}
	}()
	t.Cleanup(func() {
		close(stop)
		_ = raw.Close()
		select {
		case <-c.done:
		case <-time.After(5 * time.Second):
			t.Error("backend control reader survived cleanup")
		}
	})
	return c
}
func (c *linkgateControlClient) send(value msg.Message) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	return msg.WriteMsg(c.rw, value)
}
func (c *linkgateControlClient) publish(t *testing.T, target authority.ServiceScope, kind string) string {
	t.Helper()
	name := authority.TransportProxyName(target, kind)
	if c.send(&msg.NewProxy{ProxyName: name, ProxyType: kind, Sk: name, AllowUsers: []string{"*"}, Metas: map[string]string{"linkgate.service": target.Service}}) != nil {
		t.Fatal("actual backend publication write failed")
	}
	select {
	case reply := <-c.proxies:
		if reply.ProxyName != name || reply.Error != "" {
			t.Fatal("approved backend publication rejected")
		}
	case <-time.After(3 * time.Second):
		t.Fatal("backend publication response stalled")
	}
	return name
}
func linkgateVisitorSocket(t *testing.T, f *linkgatetest.Fixture, s *Service, device *authority.DeviceIdentity, application, run, name string) (net.Conn, string) {
	t.Helper()
	connection := f.Dial(t, s.listener.Addr().String(), device, application, "access")
	now := time.Now().Unix()
	request := &msg.NewVisitorConn{RunID: run, ProxyName: name, Timestamp: now, SignKey: util.GetAuthKey(name, now)}
	if msg.WriteMsg(connection, request) != nil {
		t.Fatal("visitor request write failed")
	}
	_ = connection.SetReadDeadline(time.Now().Add(3 * time.Second))
	value, err := msg.ReadMsg(connection)
	_ = connection.SetReadDeadline(time.Time{})
	if err != nil {
		t.Fatal("visitor response stalled")
	}
	reply, ok := value.(*msg.NewVisitorConnResp)
	if !ok {
		t.Fatal("wrong visitor response type")
	}
	return connection, reply.Error
}

func TestLinkGateActualLoginRoleNamespaceAndWorkBinding(t *testing.T) {
	f, s := startLinkGateService(t)
	pub := newLinkGateControl(t, f, s, f.Publisher, "org.example.publisher", "publish", "publisher-01")
	for _, entry := range []struct{ run, user string }{{"", pub.user}, {"wrong-user-01", "claimed-user"}} {
		connection := f.Dial(t, s.listener.Addr().String(), f.Publisher, "org.example.publisher", "publish")
		_ = msg.WriteMsg(connection, &msg.Login{RunID: entry.run, User: entry.user, ClientID: pub.user})
		_ = connection.SetReadDeadline(time.Now().Add(2 * time.Second))
		value, err := msg.ReadMsg(connection)
		if err != nil {
			t.Fatal("denied login response unavailable")
		}
		if reply, ok := value.(*msg.LoginResp); !ok || reply.Error == "" {
			t.Fatal("empty RunID or claimed namespace admitted")
		}
		_ = connection.Close()
	}
	// A same-device different-application peer knows a real RunID, but cannot
	// turn that string into ownership of the publisher's work pool.
	borrowed := f.Dial(t, s.listener.Addr().String(), f.Publisher, "org.example.other", "publish")
	_ = msg.WriteMsg(borrowed, &msg.NewWorkConn{RunID: pub.run})
	_ = borrowed.SetReadDeadline(time.Now().Add(2 * time.Second))
	value, err := msg.ReadMsg(borrowed)
	if err != nil {
		t.Fatal("denied work response unavailable")
	}
	if reply, ok := value.(*msg.StartWorkConn); !ok || reply.Error == "" {
		t.Fatal("another application borrowed work RunID")
	}
	visitor := newLinkGateControl(t, f, s, f.Visitor, "org.example.client", "access", "visitor-01")
	if visitor.send(&msg.NewProxy{ProxyName: authority.TransportProxyName(f.Target, "stcp"), ProxyType: "stcp", Sk: authority.TransportProxyName(f.Target, "stcp"), AllowUsers: []string{"*"}, Metas: map[string]string{"linkgate.service": "files"}}) != nil {
		t.Fatal("role denial request failed")
	}
	select {
	case reply := <-visitor.proxies:
		if reply.Error == "" {
			t.Fatal("access control published a proxy")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("role denial response stalled")
	}
	raw, err := net.DialTimeout("tcp4", s.listener.Addr().String(), time.Second)
	if err != nil {
		t.Fatal("fixture listener unexpectedly unavailable")
	}
	defer raw.Close()
	_ = raw.SetDeadline(time.Now().Add(2 * time.Second))
	_ = msg.WriteMsg(raw, &msg.Login{RunID: "legacy-01", User: pub.user, PrivilegeKey: "legacy-shared-token"})
	if value, e := msg.ReadMsg(raw); e == nil {
		if reply, ok := value.(*msg.LoginResp); ok && reply.Error == "" {
			t.Fatal("raw shared-token listener bypassed mTLS")
		}
	}
}

func TestLinkGateSilentHandshakeCannotBlockAnotherDevice(t *testing.T) {
	f, s := startLinkGateService(t)
	silent, err := net.DialTimeout("tcp4", s.listener.Addr().String(), time.Second)
	if err != nil {
		t.Fatal("silent peer fixture unavailable")
	}
	defer silent.Close()
	time.Sleep(50 * time.Millisecond)
	began := time.Now()
	_ = newLinkGateControl(t, f, s, f.Publisher, "org.example.publisher", "publish", "parallel-handshake")
	if time.Since(began) >= 2*time.Second {
		t.Fatal("silent unauthenticated peer blocked a valid device handshake")
	}
	_ = silent.Close()
	waitCount := func(want int) {
		deadline := time.Now().Add(2 * time.Second)
		for len(s.linkAccepts) != want {
			if time.Now().After(deadline) {
				t.Fatal("bounded handshake admission did not settle")
			}
			time.Sleep(time.Millisecond)
		}
	}
	waitCount(0)
	var held []net.Conn
	defer func() {
		for _, connection := range held {
			_ = connection.Close()
		}
	}()
	for range 32 {
		connection, e := net.DialTimeout("tcp4", s.listener.Addr().String(), time.Second)
		if e != nil {
			t.Fatal("handshake capacity fixture unavailable")
		}
		held = append(held, connection)
	}
	waitCount(32)
	overflow, e := net.DialTimeout("tcp4", s.listener.Addr().String(), time.Second)
	if e != nil {
		t.Fatal("overflow fixture unavailable")
	}
	defer overflow.Close()
	_ = overflow.SetReadDeadline(time.Now().Add(time.Second))
	_, e = overflow.Read(make([]byte, 1))
	if e == nil {
		t.Fatal("handshake overflow unexpectedly delivered bytes")
	}
	if timeout, ok := e.(net.Error); ok && timeout.Timeout() {
		t.Fatal("handshake overflow queued instead of being refused")
	}
	if len(s.linkAccepts) != 32 {
		t.Fatal("handshake admission exceeded its bound")
	}
	for _, connection := range held {
		_ = connection.Close()
	}
	waitCount(0)
	_ = newLinkGateControl(t, f, s, f.Publisher, "org.example.other", "publish", "after-handshake-overflow")
}

func TestLinkGateActualRelayGrantRevokeClosesBlockedIO(t *testing.T) {
	f, s := startLinkGateService(t)
	pub := newLinkGateControl(t, f, s, f.Publisher, "org.example.publisher", "publish", "publisher-01")
	visitor := newLinkGateControl(t, f, s, f.Visitor, "org.example.client", "access", "visitor-01")
	name := pub.publish(t, f.Target, "stcp")
	_, failure := linkgateVisitorSocket(t, f, s, f.Visitor, "org.example.other", visitor.run, name)
	if failure == "" {
		t.Fatal("different application borrowed visitor RunID")
	}
	_, failure = linkgateVisitorSocket(t, f, s, f.OtherVisitor, "org.example.client", visitor.run, name)
	if failure == "" {
		t.Fatal("different device borrowed visitor RunID")
	}
	_, failure = linkgateVisitorSocket(t, f, s, f.Visitor, "org.example.client", "", name)
	if failure == "" {
		t.Fatal("empty visitor RunID accepted")
	}
	data, failure := linkgateVisitorSocket(t, f, s, f.Visitor, "org.example.client", visitor.run, name)
	if failure != "" {
		t.Fatal("current exact-scope visitor refused")
	}
	select {
	case <-pub.work:
	case <-time.After(3 * time.Second):
		t.Fatal("actual relay never requested publisher work")
	}
	work := f.Dial(t, s.listener.Addr().String(), f.Publisher, "org.example.publisher", "publish")
	if msg.WriteMsg(work, &msg.NewWorkConn{RunID: pub.run}) != nil {
		t.Fatal("actual publisher work request failed")
	}
	_ = work.SetReadDeadline(time.Now().Add(3 * time.Second))
	reply, err := msg.ReadMsg(work)
	_ = work.SetReadDeadline(time.Time{})
	if err != nil {
		t.Fatal("actual relay work response stalled")
	}
	if started, ok := reply.(*msg.StartWorkConn); !ok || started.Error != "" || started.ProxyName != name {
		t.Fatal("actual work stream routed outside its service")
	}
	message := []byte("authorized backend ciphertext fixture")
	if _, err = data.Write(message); err != nil {
		t.Fatal("actual relay write failed")
	}
	buffer := make([]byte, len(message))
	_ = work.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, err = io.ReadFull(work, buffer); err != nil || !bytes.Equal(message, buffer) {
		t.Fatal("actual relay payload lost")
	}
	_ = work.SetReadDeadline(time.Time{})
	if _, err = work.Write(message); err != nil {
		t.Fatal("actual relay response write failed")
	}
	_ = data.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, err = io.ReadFull(data, buffer); err != nil || !bytes.Equal(message, buffer) {
		t.Fatal("actual relay response lost")
	}
	_ = data.SetReadDeadline(time.Time{})
	ended := make(chan error, 1)
	go func() { _, e := data.Read(make([]byte, 1)); ended <- e }()
	began := time.Now()
	if _, err = f.Store.SetGrant(f.Owner.ID, uuid.NewString(), f.Scope, 1, false, time.Now()); err != nil {
		t.Fatal("actual relay grant revocation failed")
	}
	select {
	case e := <-ended:
		if e == nil {
			t.Fatal("revoked relay reported a business byte")
		}
	case <-time.After(5 * time.Second):
		t.Fatal("revoked actual relay kept blocked IO alive")
	}
	if time.Since(began) >= 5*time.Second {
		t.Fatal("actual relay revoke exceeded deadline")
	}
	_, failure = linkgateVisitorSocket(t, f, s, f.Visitor, "org.example.client", visitor.run, name)
	if failure == "" {
		t.Fatal("revoked service reopened")
	}
}

func TestLinkGateActualNatHoleCannotBypassPrecheck(t *testing.T) {
	f, s := startLinkGateService(t)
	pub := newLinkGateControl(t, f, s, f.Publisher, "org.example.publisher", "publish", "publisher-01")
	visitor := newLinkGateControl(t, f, s, f.Visitor, "org.example.client", "access", "visitor-01")
	name := pub.publish(t, f.Target, "xtcp")
	if _, err := f.Store.SetGrant(f.Owner.ID, uuid.NewString(), f.Scope, 1, false, time.Now()); err != nil {
		t.Fatal("hole-punch grant revoke failed")
	}
	for _, precheck := range []bool{true, false} {
		now := time.Now().Unix()
		if visitor.send(&msg.NatHoleVisitor{TransactionID: "hole-transaction-01", ProxyName: name, PreCheck: precheck, Protocol: "quic", Timestamp: now, SignKey: util.GetAuthKey(name, now), MappedAddrs: []string{"192.0.2.1:41000", "192.0.2.1:41001"}}) != nil {
			t.Fatal("actual NatHole request write failed")
		}
		select {
		case response := <-visitor.nat:
			if response.Error == "" || response.Sid != "" {
				t.Fatal("actual NatHole bypassed current grant")
			}
		case <-time.After(2 * time.Second):
			t.Fatal("NatHole denial response stalled")
		}
	}
}

func TestLinkGateRejectsMalformedCandidateEnvelopes(t *testing.T) {
	f, s := startLinkGateService(t)
	pub := newLinkGateControl(t, f, s, f.Publisher, "org.example.publisher", "publish", "publisher-bounds")
	visitor := newLinkGateControl(t, f, s, f.Visitor, "org.example.client", "access", "visitor-bounds")
	name := pub.publish(t, f.Target, "xtcp")
	for _, mapped := range [][]string{nil, {"192.0.2.1:41000"}, {"example.invalid:41000", "192.0.2.1:41001"}, {"0.0.0.0:41000", "192.0.2.1:41001"}, {"224.0.0.1:41000", "192.0.2.1:41001"}, {"192.0.2.1:0", "192.0.2.1:41001"}} {
		if visitor.send(&msg.NatHoleVisitor{TransactionID: "candidate-bounds-01", ProxyName: name, Protocol: "quic", MappedAddrs: mapped}) != nil {
			t.Fatal("malformed fixture send failed")
		}
		select {
		case reply := <-visitor.nat:
			if reply.Error == "" || reply.Sid != "" {
				t.Fatal("malformed candidate envelope admitted")
			}
		case <-time.After(2 * time.Second):
			t.Fatal("malformed candidate denial stalled")
		}
	}
}

func TestLinkGateActualIndependentRuntimeInstances(t *testing.T) {
	f, s := startLinkGateService(t)
	first := newLinkGateInstance(t, f, s, f.Visitor, "org.example.client", "access", "instance-first-01", ".00112233445566778899aabbccddeeff")
	second := newLinkGateInstance(t, f, s, f.Visitor, "org.example.client", "access", "instance-second-01", ".ffeeddccbbaa99887766554433221100")
	if first.send(&msg.Ping{}) != nil || second.send(&msg.Ping{}) != nil {
		t.Fatal("same-application instances interfered")
	}
	namespace := authority.TransportNamespace(f.Visitor.ID, "org.example.client", "access")
	for _, client := range []string{namespace + ".", namespace + ".00112233445566778899AABBCCDDEEFF", namespace + ".00112233445566778899aabbccddeef", namespace + ".00112233445566778899aabbccddeeff00", "other.00112233445566778899aabbccddeeff"} {
		connection := f.Dial(t, s.listener.Addr().String(), f.Visitor, "org.example.client", "access")
		_ = msg.WriteMsg(connection, &msg.Login{RunID: "invalid-instance-01", User: namespace, ClientID: client})
		_ = connection.SetReadDeadline(time.Now().Add(2 * time.Second))
		value, err := msg.ReadMsg(connection)
		_ = connection.Close()
		if err != nil {
			t.Fatal("instance rejection response unavailable")
		}
		reply, ok := value.(*msg.LoginResp)
		if !ok || reply.Error == "" {
			t.Fatal("noncanonical or foreign instance admitted")
		}
	}
	select {
	case <-first.done:
		t.Fatal("first instance displaced")
	default:
	}
	select {
	case <-second.done:
		t.Fatal("second instance displaced")
	default:
	}
}
