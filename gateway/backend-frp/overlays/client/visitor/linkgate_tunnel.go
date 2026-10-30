package visitor

import (
	"context"
	"net"
	"os"
	"sync"

	v1 "github.com/fatedier/frp/pkg/config/v1"
)

// Only the owned connector can select this path; the wrapper adds no authority.
func linkgateGuardTunnel(original TunnelSession, config *v1.ClientCommonConfig, protocol string) TunnelSession {
	if os.Getenv("LINKGATE_AGENT_PIPE") == "" && os.Getenv("LINKGATE_AGENT_PID") == "" {
		return original
	}
	return &linkgateTunnelSession{factory: func() TunnelSession {
		if protocol == "kcp" {
			return NewKCPTunnelSession()
		}
		return NewQUICTunnelSession(config)
	}}
}

type linkgateTunnelSession struct {
	mu      sync.Mutex
	current TunnelSession
	factory func() TunnelSession
	closed  bool
}

func (s *linkgateTunnelSession) Init(socket *net.UDPConn, remote *net.UDPAddr) error {
	next := s.factory()
	if e := next.Init(socket, remote); e != nil {
		next.Close()
		return e
	}
	s.mu.Lock()
	closed := s.closed
	keep := !closed && s.current == nil
	if keep {
		s.current = next
	}
	s.mu.Unlock()
	if !keep {
		next.Close()
	}
	if closed {
		return net.ErrClosed
	}
	return nil
}
func (s *linkgateTunnelSession) OpenConn(ctx context.Context) (net.Conn, error) {
	s.mu.Lock()
	observed, closed := s.current, s.closed
	s.mu.Unlock()
	if closed {
		return nil, net.ErrClosed
	}
	if observed == nil {
		return nil, ErrNoTunnelSession
	}
	connection, e := observed.OpenConn(ctx)
	if e == nil {
		return connection, nil
	}
	// A stream limit or caller timeout does not prove the shared carrier failed.
	if ctx.Err() != nil {
		return nil, e
	}
	s.mu.Lock()
	retire := s.current == observed
	if retire {
		s.current = nil
	}
	s.mu.Unlock()
	if retire {
		observed.Close()
	}
	return nil, e
}
func (s *linkgateTunnelSession) Close() {
	s.mu.Lock()
	s.closed = true
	current := s.current
	s.current = nil
	s.mu.Unlock()
	if current != nil {
		current.Close()
	}
}
