package linkgatemetrics

import (
	"io"
	"net"
	"testing"
	"time"
)

func TestCountsActualReadsAndWrites(t *testing.T) {
	a, b := net.Pipe()
	defer a.Close()
	defer b.Close()
	c := Begin(a, "visitor", "direct")
	defer c.Finish()
	go func() { b.Write([]byte("request")); io.ReadFull(b, make([]byte, 5)) }()
	if _, e := io.ReadFull(c, make([]byte, 7)); e != nil {
		t.Fatal(e)
	}
	if _, e := c.Write([]byte("reply")); e != nil {
		t.Fatal(e)
	}
	if c.tx.Load() != 7 || c.rx.Load() != 5 {
		t.Fatal("incorrect application byte counts")
	}
	c.Finish()
	c.Finish()
	c.emit()
}
func TestFallbackRetainsAttemptTime(t *testing.T) {
	a, b := net.Pipe()
	defer a.Close()
	defer b.Close()
	attempt := Prepare(a)
	attempt.start = time.Now().Add(-2 * time.Second)
	attempt.Fallback("direct_tunnel_unavailable")
	c := Begin(Prepare(attempt), "visitor", "relay")
	defer c.Finish()
	if c.rec.SetupMs < 2000 || c.rec.Reason != "direct_tunnel_unavailable" {
		t.Fatal("lost fallback context")
	}
}
