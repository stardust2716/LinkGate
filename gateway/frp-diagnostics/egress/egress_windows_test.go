//go:build windows

package linkgateegress

import (
	"net"
	"strconv"
	"syscall"
	"testing"
)

func TestBindingRejectsPartialAndInvalidEnvironment(t *testing.T) {
	t.Setenv("LINKGATE_BIND_INDEX", "invalid")
	t.Setenv("LINKGATE_BIND_IPV4", "")
	t.Setenv("LINKGATE_BIND_MAC", "")
	if _, e := ListenUDP("udp4", nil, "test"); e == nil {
		t.Fatal("invalid binding accepted")
	}
}
func TestPhysicalUDPSocketOptionAndSource(t *testing.T) {
	interfaces, e := net.Interfaces()
	if e != nil {
		t.Fatal(e)
	}
	for _, iface := range interfaces {
		if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 || len(iface.HardwareAddr) == 0 {
			continue
		}
		addresses, _ := iface.Addrs()
		for _, address := range addresses {
			ip, _, _ := net.ParseCIDR(address.String())
			if ip.To4() == nil || ip.IsLinkLocalUnicast() {
				continue
			}
			t.Setenv("LINKGATE_BIND_INDEX", strconv.Itoa(iface.Index))
			t.Setenv("LINKGATE_BIND_IPV4", ip.String())
			t.Setenv("LINKGATE_BIND_MAC", iface.HardwareAddr.String())
			c, e := ListenUDP("udp4", nil, "test")
			if e != nil {
				t.Fatal(e)
			}
			defer c.Close()
			if !c.LocalAddr().(*net.UDPAddr).IP.Equal(ip) {
				t.Fatal("wrong source")
			}
			raw, _ := c.SyscallConn()
			var actual int
			var optionError error
			raw.Control(func(fd uintptr) {
				actual, optionError = syscall.GetsockoptInt(syscall.Handle(fd), syscall.IPPROTO_IP, 31)
			})
			if optionError != nil || actual != iface.Index {
				t.Fatal("interface option missing", actual, optionError)
			}
			t.Setenv("LINKGATE_BIND_MAC", "00:00:00:00:00:00")
			if _, e := ListenUDP("udp4", nil, "test"); e == nil {
				t.Fatal("changed adapter accepted")
			}
			return
		}
	}
	t.Skip("no non-loopback adapter")
}
