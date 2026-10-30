package server

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"regexp"
	"sync"

	v1 "github.com/fatedier/frp/pkg/config/v1"
	"github.com/fatedier/frp/pkg/msg"
	netpkg "github.com/fatedier/frp/pkg/util/net"
	"github.com/fatedier/frp/server/proxy"
	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
)

type linkgatePeerKey struct{}

var linkgateTransaction = regexp.MustCompile(`^[A-Za-z0-9_-]{8,64}$`)
var linkgateInstance = regexp.MustCompile(`^[0-9a-f]{32}$`)

func linkgateClientID(namespace, client string) bool {
	return client == namespace || (len(client) == len(namespace)+33 && client[:len(namespace)+1] == namespace+"." && linkgateInstance.MatchString(client[len(namespace)+1:]))
}

func linkgateCandidates(mapped, assisted []string) bool {
	if len(mapped) < 2 || len(mapped) > 8 || len(assisted) > 16 {
		return false
	}
	for _, list := range [][]string{mapped, assisted} {
		for _, value := range list {
			if len(value) > 128 {
				return false
			}
			endpoint, err := netip.ParseAddrPort(value)
			if err != nil || endpoint.Port() == 0 || endpoint.Addr().IsUnspecified() || endpoint.Addr().IsMulticast() {
				return false
			}
		}
	}
	return true
}
func linkgateHoleVisitor(request *msg.NatHoleVisitor) bool {
	return linkgateTransaction.MatchString(request.TransactionID) && (request.PreCheck || ((request.Protocol == "quic" || request.Protocol == "kcp") && linkgateCandidates(request.MappedAddrs, request.AssistedAddrs)))
}
func linkgateHoleClient(request *msg.NatHoleClient) bool {
	return linkgateTransaction.MatchString(request.TransactionID) && linkgateTransaction.MatchString(request.Sid) && linkgateCandidates(request.MappedAddrs, request.AssistedAddrs)
}

// NewService retains upstream behavior. Secure composition is explicit and
// immutable at construction; no listener can enable legacy admission on failure.
func NewService(configuration *v1.ServerConfig) (*Service, error) {
	return newService(configuration, nil)
}

func (svr *Service) AuthenticatedAddr() net.Addr {
	if svr.linkGate == nil || svr.listener == nil {
		return nil
	}
	return svr.listener.Addr()
}
func NewAuthenticatedService(configuration *v1.ServerConfig, gate *authority.TransportGate) (*Service, error) {
	if configuration == nil || gate == nil || configuration.KCPBindPort != 0 || configuration.QUICBindPort != 0 || configuration.SSHTunnelGateway.BindPort != 0 || configuration.VhostHTTPPort != 0 || configuration.VhostHTTPSPort != 0 || configuration.TCPMuxHTTPConnectPort != 0 || configuration.WebServer.Port != 0 || len(configuration.HTTPPlugins) != 0 || configuration.Auth.Method != "token" || configuration.Auth.Token != "" || configuration.Auth.TokenSource != nil {
		return nil, errors.New("INVALID_REQUEST")
	}
	return newService(configuration, gate)
}

func linkgatePeer(connection net.Conn) *authority.TransportPeer {
	value, _ := netpkg.NewContextFromConn(connection).Value(linkgatePeerKey{}).(*authority.TransportPeer)
	return value
}
func (svr *Service) linkgateLogin(connection *msg.Conn, login *msg.Login, internal bool) (*authority.TransportControl, error) {
	if svr.linkGate == nil {
		return nil, nil
	}
	peer := linkgatePeer(connection)
	if internal || peer == nil || login.ClientSpec.AlwaysAuthPass || login.ClientSpec.Type != "" || !linkgateClientID(peer.Namespace(), login.ClientID) || login.User != peer.Namespace() {
		return nil, &authority.Error{Code: "AUTH_REQUIRED"}
	}
	return svr.linkGate.Login(peer, login.RunID, login.User)
}
func (svr *Service) linkgateAccept(ctx context.Context, connection net.Conn) (context.Context, net.Conn, error) {
	bound, err := svr.linkGate.Accept(ctx, connection)
	if err != nil {
		return ctx, nil, err
	}
	ctx = context.WithValue(ctx, linkgatePeerKey{}, bound.Peer())
	return ctx, netpkg.NewContextConn(ctx, bound), nil
}

func (ctl *Control) linkgateAsync(handler func(msg.Message), visitor bool) func(msg.Message) {
	if ctl.sessionCtx.LinkGate == nil {
		return msg.AsyncHandler(handler)
	}
	return func(message msg.Message) {
		release, err := ctl.sessionCtx.LinkGate.Request(ctl.sessionCtx.LinkControl, visitor)
		if err != nil {
			if request, ok := message.(*msg.NatHoleVisitor); ok {
				_ = ctl.msgDispatcher.Send(&msg.NatHoleResp{TransactionID: request.TransactionID, Error: authority.Code(err)})
			}
			return
		}
		go func() { defer release(); handler(message) }()
	}
}

func (ctl *Control) linkgatePublication(request *msg.NewProxy) (*authority.TransportPublication, error) {
	if ctl.sessionCtx.LinkGate == nil {
		return nil, nil
	}
	if len(request.Metas) != 1 || request.Metas["linkgate.service"] == "" || request.Sk != request.ProxyName || len(request.AllowUsers) != 1 || request.AllowUsers[0] != "*" || request.UseEncryption || request.UseCompression {
		return nil, &authority.Error{Code: "AUTH_REQUIRED"}
	}
	return ctl.sessionCtx.LinkGate.Publish(ctl.sessionCtx.LinkControl, request.Metas["linkgate.service"], request.ProxyType, request.ProxyName)
}

// The compatibility STCP key is the public opaque proxy name in secure mode.
// It is not authority: mTLS subject, current scoped grant, and Agent data mTLS
// remain mandatory. Never fall back to the old shared registration credential.
func (svr *Service) linkgateVisitor(connection net.Conn, request *msg.NewVisitorConn) (net.Conn, error) {
	control, err := svr.linkGate.Bind(linkgatePeer(connection), request.RunID)
	if err != nil {
		return nil, err
	}
	if request.UseCompression || request.UseEncryption {
		return nil, &authority.Error{Code: "AUTH_REQUIRED"}
	}
	permit, err := svr.linkGate.Visit(control, request.ProxyName, "stcp")
	if err != nil {
		return nil, err
	}
	bound := &linkgateRelayConn{Conn: connection, permit: permit, closed: make(chan struct{})}
	go func() {
		select {
		case <-permit.Done():
			_ = bound.Close()
		case <-bound.closed:
		}
	}()
	return bound, nil
}

type linkgateRelayConn struct {
	net.Conn
	permit *authority.TransportAuthorization
	closed chan struct{}
	once   sync.Once
}

func (c *linkgateRelayConn) Close() error {
	c.once.Do(func() { close(c.closed); c.permit.Close(); _ = c.Conn.Close() })
	return nil
}

func (ctl *Control) linkgateWatchPublication(permit *authority.TransportPublication, pxy proxy.Proxy) {
	if permit == nil {
		return
	}
	ctl.linkPublications.Store(pxy, permit)
	go func() {
		select {
		case <-permit.Done():
		case <-ctl.doneCh:
			return
		}
		ctl.mu.Lock()
		if ctl.proxies[pxy.GetName()] != pxy {
			ctl.mu.Unlock()
			return
		}
		delete(ctl.proxies, pxy.GetName())
		if ctl.sessionCtx.ServerCfg.MaxPortsPerClient > 0 {
			ctl.portsUsedNum -= pxy.GetUsedPortsNum()
		}
		ctl.mu.Unlock()
		ctl.closeProxy(pxy)
	}()
}
func (ctl *Control) linkgateForgetPublication(pxy proxy.Proxy) {
	if value, ok := ctl.linkPublications.LoadAndDelete(pxy); ok {
		value.(*authority.TransportPublication).Close()
	}
}
