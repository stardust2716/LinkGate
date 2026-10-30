//go:build windows

package client

import (
	"context"
	"errors"
	"net"
	"regexp"
	"sync"
	"unsafe"

	"github.com/Microsoft/go-winio"
	"golang.org/x/sys/windows"
)

var linkgatePipePattern = regexp.MustCompile(`^\\\\\.\\pipe\\LinkGate\.runtime\.v1\.[0-9a-f]{32}$`)
var linkgatePipeServerPID = windows.NewLazySystemDLL("kernel32.dll").NewProc("GetNamedPipeServerProcessId")

type linkgateWindowsConn struct {
	net.Conn
	parent windows.Handle
	once   sync.Once
}

func (c *linkgateWindowsConn) Close() error {
	var err error
	c.once.Do(func() { err = c.Conn.Close(); _ = windows.CloseHandle(c.parent) })
	return err
}
func linkgateDial(ctx context.Context, path string, parentPID int) (net.Conn, error) {
	if !linkgatePipePattern.MatchString(path) {
		return nil, errors.New("AGENT_IDENTITY_REQUIRED")
	}
	parent, err := windows.OpenProcess(windows.SYNCHRONIZE|windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(parentPID))
	if err != nil {
		return nil, errors.New("AGENT_IDENTITY_REQUIRED")
	}
	keep := false
	defer func() {
		if !keep {
			_ = windows.CloseHandle(parent)
		}
	}()
	if state, e := windows.WaitForSingleObject(parent, 0); e != nil || state != uint32(windows.WAIT_TIMEOUT) {
		return nil, errors.New("AGENT_IDENTITY_REQUIRED")
	}
	connection, err := winio.DialPipeContext(ctx, path)
	if err != nil {
		return nil, errors.New("AGENT_UNAVAILABLE")
	}
	fail := func() (net.Conn, error) { _ = connection.Close(); return nil, errors.New("AGENT_IDENTITY_REQUIRED") }
	file, ok := connection.(interface{ Fd() uintptr })
	if !ok {
		return fail()
	}
	var actual uint32
	result, _, _ := linkgatePipeServerPID.Call(file.Fd(), uintptr(unsafe.Pointer(&actual)))
	if result == 0 || actual != uint32(parentPID) {
		return fail()
	}
	if state, e := windows.WaitForSingleObject(parent, 0); e != nil || state != uint32(windows.WAIT_TIMEOUT) {
		return fail()
	}
	keep = true
	return &linkgateWindowsConn{Conn: connection, parent: parent}, nil
}
