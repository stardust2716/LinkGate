package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"os/signal"
	"syscall"
	"time"

	v1 "github.com/fatedier/frp/pkg/config/v1"
	frplog "github.com/fatedier/frp/pkg/util/log"
	"github.com/fatedier/frp/server"
	goliblog "github.com/fatedier/golib/log"
	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
	"github.com/stardust2716/Redesk/gateway/control-plane/serverhost"
)

func main() {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	// Ordinary runtime logs can contain candidate addresses or client input. Only
	// this command's fixed aggregate records are written to the service journal.
	frplog.Logger = goliblog.New(goliblog.WithOutput(io.Discard))
	if e := execute(ctx, os.Args[1:], os.Stdout); e != nil {
		fmt.Fprintln(os.Stderr, code(e))
		os.Exit(1)
	}
}
func code(e error) string {
	for _, fixed := range []error{serverhost.ErrConfiguration, serverhost.ErrStorage, serverhost.ErrIdentity, serverhost.ErrPlatform, serverhost.ErrClock} {
		if errors.Is(e, fixed) {
			return fixed.Error()
		}
	}
	return "SERVER_FAILED"
}
func execute(ctx context.Context, args []string, output io.Writer) error {
	if len(args) == 2 && args[0] == "request" {
		if e := serverhost.CreateRequest(args[1]); e != nil {
			return e
		}
		_, e := fmt.Fprintln(output, `{"created":true}`)
		return e
	}
	if len(args) < 2 || args[0] != "init" && args[0] != "serve" || args[0] == "init" && len(args) != 3 || args[0] == "serve" && len(args) != 2 {
		return serverhost.ErrConfiguration
	}
	config, e := serverhost.LoadConfig(args[1])
	if e != nil {
		return e
	}
	if args[0] == "init" {
		owner, e := serverhost.ReadOwnerCertificate(args[2])
		if e != nil {
			return e
		}
		if e = serverhost.Initialize(config, owner); e != nil {
			return e
		}
		_, e = fmt.Fprintln(output, `{"initialized":true}`)
		return e
	}
	return serve(ctx, config, output)
}
func serve(ctx context.Context, config serverhost.Config, output io.Writer) error {
	host, e := serverhost.Open(config)
	if e != nil {
		return e
	}
	defer host.Close()
	control, e := authority.NewControlServer(host.Identity, host.Store, time.Now)
	if e != nil {
		return serverhost.ErrIdentity
	}
	defer control.Close()
	listener, e := net.Listen("tcp4", config.ControlAddress)
	if e != nil {
		return errors.New("listener unavailable")
	}
	defer listener.Close()
	gate, e := authority.NewTransportGate(host.Identity, host.Store, time.Now)
	if e != nil {
		return serverhost.ErrIdentity
	}
	defer gate.Close()
	endpoint, _ := netip.ParseAddrPort(config.BackendAddress)
	configuration := &v1.ServerConfig{}
	if configuration.Complete() != nil {
		return serverhost.ErrConfiguration
	}
	configuration.BindAddr = endpoint.Addr().String()
	configuration.BindPort = int(endpoint.Port())
	configuration.ProxyBindAddr = "127.0.0.1"
	configuration.Transport.HeartbeatTimeout = 3
	backend, e := server.NewAuthenticatedService(configuration, gate)
	if e != nil {
		return errors.New("backend unavailable")
	}
	defer backend.Close()
	running, cancel := context.WithCancel(ctx)
	defer cancel()
	controlDone := make(chan error, 1)
	backendDone := make(chan struct{})
	go func() { controlDone <- control.Serve(listener) }()
	go func() { backend.Run(running); close(backendDone) }()
	defer func() { cancel(); backend.Close(); control.Close(); gate.Close(); <-backendDone }()
	if _, e = fmt.Fprintln(output, `{"ready":true,"authenticated":true}`); e != nil {
		return errors.New("readiness unavailable")
	}
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return nil
		case <-controlDone:
			return errors.New("control stopped")
		case <-backendDone:
			return errors.New("backend stopped")
		case <-ticker.C:
			if e = host.Check(); e != nil {
				return e
			}
		}
	}
}
