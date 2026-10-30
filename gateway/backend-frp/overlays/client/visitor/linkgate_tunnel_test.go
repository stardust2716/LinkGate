package visitor

import (
	"context"
	"errors"
	"net"
	"sync/atomic"
	"testing"
	"time"
)

type linkgateFakeTunnel struct {
	closed atomic.Int32
	opens  atomic.Int32
	init   func() error
	open   func(context.Context, int32) (net.Conn, error)
}

func (f *linkgateFakeTunnel) Init(*net.UDPConn, *net.UDPAddr) error {
	if f.init != nil {
		return f.init()
	}
	return nil
}
func (f *linkgateFakeTunnel) OpenConn(ctx context.Context) (net.Conn, error) {
	return f.open(ctx, f.opens.Add(1))
}
func (f *linkgateFakeTunnel) Close() { f.closed.Add(1) }
func linkgateGoodOpen(context.Context, int32) (net.Conn, error) {
	left, right := net.Pipe()
	right.Close()
	return left, nil
}
func TestLinkGateTunnelDuplicateInitKeepsLiveCarrier(t *testing.T) {
	first, second := &linkgateFakeTunnel{open: linkgateGoodOpen}, &linkgateFakeTunnel{open: linkgateGoodOpen}
	var index atomic.Int32
	s := &linkgateTunnelSession{factory: func() TunnelSession {
		if index.Add(1) == 1 {
			return first
		}
		return second
	}}
	defer s.Close()
	if s.Init(nil, nil) != nil || s.Init(nil, nil) != nil {
		t.Fatal("owned initialization")
	}
	connection, e := s.OpenConn(context.Background())
	if e != nil {
		t.Fatal("healthy carrier lost")
	}
	connection.Close()
	if first.closed.Load() != 0 || second.closed.Load() != 1 || first.opens.Load() != 1 {
		t.Fatal("duplicate completion replaced live carrier")
	}
}
func TestLinkGateTunnelLateFailureCannotCloseReplacement(t *testing.T) {
	entered, release := make(chan struct{}), make(chan struct{})
	first := &linkgateFakeTunnel{open: func(_ context.Context, count int32) (net.Conn, error) {
		if count == 1 {
			close(entered)
			<-release
		}
		return nil, errors.New("carrier lost")
	}}
	second := &linkgateFakeTunnel{open: linkgateGoodOpen}
	var index atomic.Int32
	s := &linkgateTunnelSession{factory: func() TunnelSession {
		if index.Add(1) == 1 {
			return first
		}
		return second
	}}
	defer s.Close()
	if s.Init(nil, nil) != nil {
		t.Fatal("init")
	}
	done := make(chan struct{})
	go func() { defer close(done); _, _ = s.OpenConn(context.Background()) }()
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("missing old attempt")
	}
	_, _ = s.OpenConn(context.Background())
	if s.Init(nil, nil) != nil {
		t.Fatal("replacement init")
	}
	close(release)
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("late attempt did not finish")
	}
	connection, e := s.OpenConn(context.Background())
	if e != nil {
		t.Fatal("late failure closed newer generation")
	}
	connection.Close()
	if second.closed.Load() != 0 || first.closed.Load() != 1 {
		t.Fatal("carrier ownership lost")
	}
}
func TestLinkGateTunnelCancelledStreamPreservesSharedCarrier(t *testing.T) {
	carrier := &linkgateFakeTunnel{open: func(ctx context.Context, _ int32) (net.Conn, error) {
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		return linkgateGoodOpen(ctx, 0)
	}}
	s := &linkgateTunnelSession{factory: func() TunnelSession { return carrier }}
	defer s.Close()
	if s.Init(nil, nil) != nil {
		t.Fatal("init")
	}
	visitor := &XTCPVisitor{session: s, startTunnelCh: make(chan struct{}, 1)}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, e := visitor.getTunnelConn(ctx); !errors.Is(e, context.Canceled) {
		t.Fatal("cancelled stream result")
	}
	connection, e := visitor.getTunnelConn(context.Background())
	if e != nil {
		t.Fatal("stream cancellation retired shared carrier")
	}
	connection.Close()
	if carrier.closed.Load() != 0 || len(visitor.startTunnelCh) != 0 {
		t.Fatal("cancelled stream restarted or closed shared carrier")
	}
}
func TestLinkGateTunnelOwnerCloseDisposesLateInit(t *testing.T) {
	entered, release := make(chan struct{}), make(chan struct{})
	carrier := &linkgateFakeTunnel{open: linkgateGoodOpen, init: func() error { close(entered); <-release; return nil }}
	s := &linkgateTunnelSession{factory: func() TunnelSession { return carrier }}
	done := make(chan error, 1)
	go func() { done <- s.Init(nil, nil) }()
	<-entered
	s.Close()
	close(release)
	select {
	case e := <-done:
		if !errors.Is(e, net.ErrClosed) {
			t.Fatal("late init survived owner close")
		}
	case <-time.After(time.Second):
		t.Fatal("late init cleanup")
	}
	if carrier.closed.Load() != 1 {
		t.Fatal("late carrier not disposed")
	}
}
