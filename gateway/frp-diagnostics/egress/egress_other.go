//go:build !windows

package linkgateegress

import (
	"context"
	"crypto/tls"
	"errors"
	"net"
	"time"
)

func Active() bool                        { return false }
func LocalIPs(original []string) []string { return original }
func ListenUDP(network string, local *net.UDPAddr, kind string) (*net.UDPConn, error) {
	return net.ListenUDP(network, local)
}
func DialUDP(network string, local, remote *net.UDPAddr) (*net.UDPConn, error) {
	return net.DialUDP(network, local, remote)
}
func DialServer(context.Context, string, *tls.Config, time.Duration, time.Duration, func(context.Context, net.Conn, string) (context.Context, net.Conn, error)) (net.Conn, error) {
	return nil, errors.New("physical_binding_windows_only")
}
