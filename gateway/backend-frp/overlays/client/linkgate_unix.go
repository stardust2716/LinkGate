//go:build linux || android

package client

import (
	"context"
	"errors"
	"net"
	"os"
	"path/filepath"

	"golang.org/x/sys/unix"
)

func linkgateDial(ctx context.Context, path string, parentPID int) (net.Conn, error) {
	if !filepath.IsAbs(path) || filepath.Clean(path) != path {
		return nil, errors.New("AGENT_IDENTITY_REQUIRED")
	}
	connection, err := (&net.Dialer{}).DialContext(ctx, "unix", path)
	if err != nil {
		return nil, errors.New("AGENT_UNAVAILABLE")
	}
	fail := func() (net.Conn, error) { _ = connection.Close(); return nil, errors.New("AGENT_IDENTITY_REQUIRED") }
	local, ok := connection.(*net.UnixConn)
	if !ok {
		return fail()
	}
	raw, err := local.SyscallConn()
	if err != nil {
		return fail()
	}
	valid := false
	if raw.Control(func(fd uintptr) {
		credentials, e := unix.GetsockoptUcred(int(fd), unix.SOL_SOCKET, unix.SO_PEERCRED)
		valid = e == nil && credentials.Pid == int32(parentPID) && credentials.Uid == uint32(os.Getuid())
	}) != nil || !valid {
		return fail()
	}
	return connection, nil
}
