// Copyright 2026 LinkGate contributors. Licensed under Apache-2.0.
// Counts plaintext application bytes only, never QUIC/TLS/control overhead.
package linkgatemetrics

import (
	"encoding/json"
	"fmt"
	"net"
	"sync"
	"sync/atomic"
	"time"
)

var serial atomic.Uint64
var output sync.Mutex

type Record struct {
	V         int    `json:"v"`
	ID        uint64 `json:"id"`
	Role      string `json:"role"`
	Path      string `json:"path"`
	Phase     string `json:"phase"`
	Tx        uint64 `json:"tx"`
	Rx        uint64 `json:"rx"`
	SetupMs   int64  `json:"setupMs"`
	ElapsedMs int64  `json:"elapsedMs"`
	Reason    string `json:"reason"`
}

// Attempt survives TransferConn, preserving the full direct+fallback setup time.
type Attempt struct {
	net.Conn
	start  time.Time
	reason string
	id     uint64
	begun  atomic.Bool
}

func Prepare(c net.Conn) *Attempt {
	if a, ok := c.(*Attempt); ok {
		return a
	}
	return &Attempt{Conn: c, start: time.Now(), id: serial.Add(1)}
}
func (a *Attempt) Fallback(reason string) { a.reason = reason }
func (a *Attempt) Fail(role string) {
	if !a.begun.CompareAndSwap(false, true) {
		return
	}
	reason := a.reason
	if reason == "" {
		reason = "connection_setup_failed"
	} else {
		reason = "direct_unavailable_relay_failed"
	}
	output.Lock()
	defer output.Unlock()
	b, _ := json.Marshal(Record{V: 1, ID: a.id, Role: role, Path: "unknown", Phase: "failed", Reason: reason, SetupMs: time.Since(a.start).Milliseconds(), ElapsedMs: time.Since(a.start).Milliseconds()})
	fmt.Printf("LINKGATE_METRIC %s\n", b)
}
func (a *Attempt) CloseWithError(err error) error {
	if c, ok := a.Conn.(interface{ CloseWithError(error) error }); ok {
		return c.CloseWithError(err)
	}
	return a.Conn.Close()
}

type Counter struct {
	net.Conn
	tx, rx atomic.Uint64
	rec    Record
	start  time.Time
	done   chan struct{}
	once   sync.Once
	mu     sync.Mutex
	closed bool
}

func BeginPublisher(c net.Conn, path string, start time.Time) *Counter {
	a := Prepare(c)
	a.start = start
	return Begin(a, "publisher", path)
}
func Begin(c net.Conn, role, path string) *Counter {
	a := Prepare(c)
	a.begun.Store(true)
	n := &Counter{Conn: a, start: a.start, done: make(chan struct{}), rec: Record{
		V: 1, ID: a.id, Role: role, Path: path, Phase: "open", SetupMs: time.Since(a.start).Milliseconds(), Reason: a.reason}}
	n.emit()
	go func() {
		timer := time.NewTicker(time.Second)
		defer timer.Stop()
		for {
			select {
			case <-timer.C:
				n.emit()
			case <-n.done:
				return
			}
		}
	}()
	return n
}
func (c *Counter) Read(p []byte) (int, error) {
	n, e := c.Conn.Read(p)
	c.tx.Add(uint64(n))
	return n, e
}
func (c *Counter) Write(p []byte) (int, error) {
	n, e := c.Conn.Write(p)
	c.rx.Add(uint64(n))
	return n, e
}
func (c *Counter) Finish() {
	c.once.Do(func() { close(c.done); c.emitFinal() })
}
func (c *Counter) emitFinal() { c.write("closed") }
func (c *Counter) emit()      { c.write("open") }
func (c *Counter) write(phase string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return
	}
	if phase == "closed" {
		c.closed = true
	}
	output.Lock()
	defer output.Unlock()
	r := c.rec
	r.Phase = phase
	r.Tx = c.tx.Load()
	r.Rx = c.rx.Load()
	r.ElapsedMs = time.Since(c.start).Milliseconds()
	b, _ := json.Marshal(r)
	fmt.Printf("LINKGATE_METRIC %s\n", b)
}
