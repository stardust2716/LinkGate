package nathole

import (
	"testing"
	"time"

	"github.com/fatedier/frp/internal/linkgatetest"
	"github.com/fatedier/frp/pkg/msg"
	"github.com/fatedier/frp/pkg/transport"
	"github.com/fatedier/frp/pkg/util/util"
	"github.com/google/uuid"
	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
)

type linkgateSender struct{ messages chan msg.Message }

func (s *linkgateSender) Send(message msg.Message) error {
	select {
	case s.messages <- message:
	default:
	}
	return nil
}
func linkgateTransport() (*linkgateSender, transport.MessageTransporter) {
	sender := &linkgateSender{make(chan msg.Message, 16)}
	return sender, transport.NewMessageTransporter(sender)
}
func linkgatePermit(t *testing.T) (*linkgatetest.Fixture, *Controller, *authority.TransportAuthorization, string) {
	t.Helper()
	f := linkgatetest.New(t)
	publisher := f.Control(t, f.Publisher, "org.example.publisher", "publish", "publisher-01")
	visitor := f.Control(t, f.Visitor, "org.example.client", "access", "visitor-01")
	name := authority.TransportProxyName(f.Target, "xtcp")
	if _, err := f.Gate.Publish(publisher, "files", "xtcp", name); err != nil {
		t.Fatal("hole-punch publication fixture failed")
	}
	permit, err := f.Gate.Visit(visitor, name, "xtcp")
	if err != nil {
		t.Fatal("hole-punch authority fixture failed")
	}
	controller, err := NewController(time.Hour)
	if err != nil {
		t.Fatal("hole-punch controller fixture failed")
	}
	controller.RequireLinkGate()
	t.Cleanup(permit.Close)
	return f, controller, permit, name
}
func linkgateVisitorMessage(name string) *msg.NatHoleVisitor {
	now := time.Now().Unix()
	return &msg.NatHoleVisitor{TransactionID: "visitor-transaction-01", ProxyName: name, Protocol: "quic", Timestamp: now, SignKey: util.GetAuthKey(name, now), MappedAddrs: []string{"192.0.2.1:41000", "192.0.2.1:41000"}, AssistedAddrs: []string{"10.0.0.1:41000"}}
}

func TestLinkGateActualHoleSessionSubjectAndRevocation(t *testing.T) {
	f, controller, permit, name := linkgatePermit(t)
	sidSource, err := controller.ListenClient(name, name, []string{"*"})
	if err != nil {
		t.Fatal("hole-punch publisher registration failed")
	}
	visitorOut, visitorTransport := linkgateTransport()
	_, clientTransport := linkgateTransport()
	finished := make(chan struct{})
	go func() {
		defer close(finished)
		controller.HandleAuthorizedVisitor(linkgateVisitorMessage(name), visitorTransport, permit.VisitorNamespace(), permit)
	}()
	var sid string
	select {
	case sid = <-sidSource:
	case <-time.After(3 * time.Second):
		t.Fatal("actual hole-punch session did not start")
	}
	response := &msg.NatHoleClient{TransactionID: "publisher-transaction-01", ProxyName: name, Sid: sid, MappedAddrs: []string{"198.51.100.1:42000", "198.51.100.1:42000"}}
	controller.HandleAuthorizedClient(response, clientTransport, authority.TransportNamespace(f.OtherPublisher.ID, "org.example.publisher", "publish"))
	controller.HandleClient(response, clientTransport)
	wrong := *response
	wrong.ProxyName = authority.TransportProxyName(authority.ServiceScope{PublisherDevice: f.OtherPublisher.ID, PublisherApp: "org.example.publisher", Service: "files"}, "xtcp")
	controller.HandleAuthorizedClient(&wrong, clientTransport, permit.PublisherNamespace())
	controller.mu.RLock()
	session := controller.sessions[sid]
	untouched := session != nil && session.clientMsg == nil
	controller.mu.RUnlock()
	if !untouched {
		t.Fatal("different publisher/proxy or legacy path injected a hole-punch response")
	}
	if _, err = f.Store.SetGrant(f.Owner.ID, uuid.NewString(), f.Scope, 1, false, time.Now()); err != nil {
		t.Fatal("hole-punch revocation failed")
	}
	select {
	case <-finished:
	case <-time.After(5 * time.Second):
		t.Fatal("revoked hole-punch session remained pending")
	}
	controller.HandleAuthorizedClient(response, clientTransport, permit.PublisherNamespace())
	controller.HandleAuthorizedReport(&msg.NatHoleReport{Sid: sid, Success: true}, permit.PublisherNamespace())
	controller.mu.RLock()
	_, exists := controller.sessions[sid]
	controller.mu.RUnlock()
	if exists || len(visitorOut.messages) != 0 {
		t.Fatal("delayed hole-punch response revived removed session or candidates")
	}
}

func TestLinkGateHoleNotificationWaitStopsWhenAuthorityCloses(t *testing.T) {
	_, controller, permit, name := linkgatePermit(t)
	if _, err := controller.ListenClient(name, name, []string{"*"}); err != nil {
		t.Fatal("hole-punch publisher registration failed")
	}
	// Deliberately do not consume the unbuffered publisher SID notification.
	// Revocation must interrupt this earlier wait, not only the later reply wait.
	_, transporter := linkgateTransport()
	finished := make(chan struct{})
	go func() {
		defer close(finished)
		controller.HandleAuthorizedVisitor(linkgateVisitorMessage(name), transporter, permit.VisitorNamespace(), permit)
	}()
	deadline := time.Now().Add(2 * time.Second)
	for {
		controller.mu.RLock()
		pending := len(controller.sessions) > 0
		controller.mu.RUnlock()
		if pending {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("hole-punch pending fixture did not start")
		}
		time.Sleep(5 * time.Millisecond)
	}
	permit.Close()
	select {
	case <-finished:
	case <-time.After(2 * time.Second):
		t.Fatal("closed authority left publisher notification blocked")
	}
}

func TestLinkGateHolePrecheckAndActualRequireRealAuthority(t *testing.T) {
	_, controller, permit, name := linkgatePermit(t)
	if _, err := controller.ListenClient(name, name, []string{"*"}); err != nil {
		t.Fatal("hole-punch publisher registration failed")
	}
	for _, precheck := range []bool{true, false} {
		out, transporter := linkgateTransport()
		request := linkgateVisitorMessage(name)
		request.PreCheck = precheck
		controller.HandleVisitor(request, transporter, permit.VisitorNamespace())
		select {
		case value := <-out.messages:
			reply, ok := value.(*msg.NatHoleResp)
			if !ok || reply.Error == "" || reply.Sid != "" {
				t.Fatal("legacy hook bypassed hole-punch authority")
			}
		case <-time.After(time.Second):
			t.Fatal("legacy hole-punch denial unavailable")
		}
	}
}

func TestLinkGateAuthorizationCannotBeRecombinedWithAnotherProxy(t *testing.T) {
	_, controller, permit, name := linkgatePermit(t)
	request := linkgateVisitorMessage(name + "-other")
	request.PreCheck = true
	out, transporter := linkgateTransport()
	controller.HandleAuthorizedVisitor(request, transporter, permit.VisitorNamespace(), permit)
	select {
	case message := <-out.messages:
		response, ok := message.(*msg.NatHoleResp)
		if !ok || response.Error == "" || response.Sid != "" {
			t.Fatal("authorization reused for another publication")
		}
	case <-time.After(time.Second):
		t.Fatal("scope mismatch denial unavailable")
	}
}

func TestLinkGateAuthorizedHoleExchangeAcceptsOnePublisherReply(t *testing.T) {
	_, controller, permit, name := linkgatePermit(t)
	sidSource, err := controller.ListenClient(name, name, []string{"*"})
	if err != nil {
		t.Fatal("hole-punch publisher registration failed")
	}
	visitorOut, visitorTransport := linkgateTransport()
	clientOut, clientTransport := linkgateTransport()
	finished := make(chan struct{})
	go func() {
		defer close(finished)
		controller.HandleAuthorizedVisitor(linkgateVisitorMessage(name), visitorTransport, permit.VisitorNamespace(), permit)
	}()
	var sid string
	select {
	case sid = <-sidSource:
	case <-time.After(3 * time.Second):
		t.Fatal("authorized hole-punch session stalled")
	}
	first := &msg.NatHoleClient{TransactionID: "publisher-transaction-01", ProxyName: name, Sid: sid, MappedAddrs: []string{"198.51.100.1:42000", "198.51.100.1:42000"}, AssistedAddrs: []string{"10.0.0.2:42000"}}
	controller.HandleAuthorizedClient(first, clientTransport, permit.PublisherNamespace())
	duplicate := *first
	duplicate.MappedAddrs = []string{"203.0.113.9:43000", "203.0.113.9:43001"}
	controller.HandleAuthorizedClient(&duplicate, clientTransport, permit.PublisherNamespace())
	controller.mu.RLock()
	session := controller.sessions[sid]
	same := session != nil && session.clientMsg == first
	controller.mu.RUnlock()
	if !same {
		t.Fatal("duplicate publisher reply replaced actual session identity or candidates")
	}
	for _, out := range []*linkgateSender{visitorOut, clientOut} {
		select {
		case message := <-out.messages:
			reply, ok := message.(*msg.NatHoleResp)
			if !ok || reply.Error != "" || reply.Sid != sid || len(reply.CandidateAddrs) == 0 {
				t.Fatal("authorized actual hole-punch exchange rejected")
			}
		case <-time.After(4 * time.Second):
			t.Fatal("authorized actual candidate response stalled")
		}
	}
	controller.HandleAuthorizedReport(&msg.NatHoleReport{Sid: sid, Success: true}, "unrelated-subject")
	controller.HandleReport(&msg.NatHoleReport{Sid: sid, Success: true})
	controller.mu.RLock()
	count := len(session.linkgateReporters)
	controller.mu.RUnlock()
	if count != 0 {
		t.Fatal("unrelated or legacy report affected authorized session")
	}
	controller.HandleAuthorizedReport(&msg.NatHoleReport{Sid: sid, Success: true}, permit.VisitorNamespace())
	controller.HandleAuthorizedReport(&msg.NatHoleReport{Sid: sid, Success: true}, permit.VisitorNamespace())
	controller.mu.RLock()
	count = len(session.linkgateReporters)
	controller.mu.RUnlock()
	if count != 1 {
		t.Fatal("authorized reports were not bounded by session side")
	}
	permit.Close()
	select {
	case <-finished:
	case <-time.After(3 * time.Second):
		t.Fatal("authorized hole-punch fixture did not stop")
	}
}
