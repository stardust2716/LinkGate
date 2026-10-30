//go:build !windows && !linux && !android

package client

import (
	"context"
	"errors"
	"net"
)

func linkgateDial(context.Context, string, int) (net.Conn, error) {
	return nil, errors.New("AGENT_BRIDGE_UNSUPPORTED")
}
