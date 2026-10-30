//go:build windows

// Copyright 2026 LinkGate contributors. Apache-2.0.
package linkgateegress

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"math/bits"
	"net"
	"os"
	"strconv"
	"sync"
	"syscall"
	"time"
)

type Binding struct {
	Index int
	IP    net.IP
	MAC   string
}

var emitted sync.Map

func configured() (Binding, error) {
	index, ip, mac := os.Getenv("LINKGATE_BIND_INDEX"), os.Getenv("LINKGATE_BIND_IPV4"), os.Getenv("LINKGATE_BIND_MAC")
	if index == "" && ip == "" && mac == "" {
		return Binding{}, nil
	}
	n, e := strconv.Atoi(index)
	address := net.ParseIP(ip)
	if e != nil || n <= 0 || n > 0xffffff || address == nil || address.To4() == nil || address.IsLoopback() || address.IsUnspecified() || address.IsMulticast() || address.IsLinkLocalUnicast() || mac == "" {
		return Binding{}, errors.New("physical_binding_invalid")
	}
	iface, e := net.InterfaceByIndex(n)
	if e != nil || iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 || iface.HardwareAddr.String() != mac {
		return Binding{}, errors.New("physical_interface_changed")
	}
	addrs, e := iface.Addrs()
	if e != nil {
		return Binding{}, errors.New("physical_address_unavailable")
	}
	for _, a := range addrs {
		candidate, _, e := net.ParseCIDR(a.String())
		if e == nil && candidate.Equal(address) {
			return Binding{n, address.To4(), mac}, nil
		}
	}
	return Binding{}, errors.New("physical_address_changed")
}
func Active() bool {
	return os.Getenv("LINKGATE_BIND_INDEX") != "" || os.Getenv("LINKGATE_BIND_IPV4") != "" || os.Getenv("LINKGATE_BIND_MAC") != ""
}
func LocalIPs(original []string) []string {
	b, e := configured()
	if e == nil && b.Index > 0 {
		return []string{b.IP.String()}
	}
	return original
}

func control(b Binding, kind string) func(string, string, syscall.RawConn) error {
	return func(network, address string, raw syscall.RawConn) error {
		current, e := configured()
		if e != nil {
			return e
		}
		if current.Index != b.Index || !current.IP.Equal(b.IP) {
			return errors.New("physical_binding_changed")
		}
		var socketErr error
		e = raw.Control(func(fd uintptr) {
			// Set requires network byte order; Get returns host byte order (Winsock).
			socketErr = syscall.SetsockoptInt(syscall.Handle(fd), syscall.IPPROTO_IP, 31, int(bits.ReverseBytes32(uint32(b.Index))))
			if socketErr != nil {
				return
			}
			var actual int
			actual, socketErr = syscall.GetsockoptInt(syscall.Handle(fd), syscall.IPPROTO_IP, 31)
			if socketErr == nil && actual != b.Index {
				socketErr = errors.New("physical_interface_verification_failed")
			}
		})
		if e != nil {
			return e
		}
		return socketErr
	}
}
func announce(b Binding, kind string) {
	if _, loaded := emitted.LoadOrStore(kind, true); loaded {
		return
	}
	value, _ := json.Marshal(map[string]interface{}{"v": 1, "mode": "physical-ipv4", "kind": kind, "interfaceIndex": b.Index})
	fmt.Printf("LINKGATE_EGRESS %s\n", value)
}
func ListenUDP(network string, local *net.UDPAddr, kind string) (*net.UDPConn, error) {
	b, e := configured()
	if e != nil {
		return nil, e
	}
	if b.Index == 0 {
		return net.ListenUDP(network, local)
	}
	if network != "udp4" {
		return nil, errors.New("physical_ipv4_required")
	}
	port := 0
	if local != nil {
		port = local.Port
		if local.IP != nil && !local.IP.IsUnspecified() && !local.IP.Equal(b.IP) {
			return nil, errors.New("physical_source_mismatch")
		}
	}
	lc := net.ListenConfig{Control: control(b, kind)}
	c, e := lc.ListenPacket(context.Background(), "udp4", net.JoinHostPort(b.IP.String(), strconv.Itoa(port)))
	if e != nil {
		return nil, e
	}
	udp, ok := c.(*net.UDPConn)
	if !ok {
		c.Close()
		return nil, errors.New("physical_udp_type")
	}
	if !udp.LocalAddr().(*net.UDPAddr).IP.Equal(b.IP) {
		udp.Close()
		return nil, errors.New("physical_source_verification_failed")
	}
	announce(b, kind)
	return udp, nil
}
func DialUDP(network string, local, remote *net.UDPAddr) (*net.UDPConn, error) {
	b, e := configured()
	if e != nil {
		return nil, e
	}
	if b.Index == 0 {
		return net.DialUDP(network, local, remote)
	}
	if remote == nil || remote.IP.To4() == nil {
		return nil, errors.New("physical_ipv4_required")
	}
	port := 0
	if local != nil {
		port = local.Port
	}
	d := net.Dialer{LocalAddr: &net.UDPAddr{IP: b.IP, Port: port}, Control: control(b, "kcp")}
	c, e := d.DialContext(context.Background(), "udp4", remote.String())
	if e != nil {
		return nil, e
	}
	udp, ok := c.(*net.UDPConn)
	if !ok {
		c.Close()
		return nil, errors.New("physical_udp_type")
	}
	announce(b, "kcp")
	return udp, nil
}
func DialServer(ctx context.Context, address string, tlsConfig *tls.Config, timeout, keepalive time.Duration, head func(context.Context, net.Conn, string) (context.Context, net.Conn, error)) (net.Conn, error) {
	b, e := configured()
	if e != nil {
		return nil, e
	}
	if b.Index == 0 {
		return nil, errors.New("physical_binding_required")
	}
	d := net.Dialer{LocalAddr: &net.TCPAddr{IP: b.IP}, Control: control(b, "tcp"), Timeout: timeout, KeepAlive: keepalive}
	c, e := d.DialContext(ctx, "tcp4", address)
	if e != nil {
		return nil, e
	}
	if !c.LocalAddr().(*net.TCPAddr).IP.Equal(b.IP) {
		c.Close()
		return nil, errors.New("physical_source_verification_failed")
	}
	_, next, e := head(ctx, c, address)
	if e != nil {
		c.Close()
		return nil, e
	}
	c = next
	if tlsConfig != nil {
		c = tls.Client(c, tlsConfig)
	}
	announce(b, "tcp")
	return c, nil
}
