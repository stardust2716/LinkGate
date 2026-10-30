package main

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json/v2"
	"encoding/pem"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/fatedier/frp/internal/linkgatetest"
	frplog "github.com/fatedier/frp/pkg/util/log"
	goliblog "github.com/fatedier/golib/log"
	"github.com/google/uuid"
	"github.com/stardust2716/Redesk/gateway/control-plane/serverhost"
)

type readyWriter struct {
	once  sync.Once
	ready chan struct{}
}

func (w *readyWriter) Write(value []byte) (int, error) {
	if string(value) != "{\"ready\":true,\"authenticated\":true}\n" {
		return 0, io.ErrUnexpectedEOF
	}
	w.once.Do(func() { close(w.ready) })
	return len(value), nil
}
func serverFixture(t *testing.T) (serverhost.Config, *linkgatetest.Fixture, string, string) {
	t.Helper()
	if runtime.GOOS != "linux" {
		t.Skip("persistent server host requires Linux")
	}
	frplog.Logger = goliblog.New(goliblog.WithOutput(io.Discard))
	f := linkgatetest.New(t)
	directory := t.TempDir()
	identity, state, witness := filepath.Join(directory, "identity"), filepath.Join(directory, "authority"), filepath.Join(directory, "witness")
	for _, path := range []string{identity, state, witness} {
		if os.Mkdir(path, 0700) != nil {
			t.Fatal("fixture directory")
		}
	}
	key, e := x509.MarshalPKCS8PrivateKey(f.Server.Certificate.PrivateKey)
	if e != nil {
		t.Fatal("fixture key")
	}
	for name, data := range map[string][]byte{"device-cert.pem": pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: f.Server.Leaf.Raw}), "device-key.pem": pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: key}), "owner-root.pem": pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: f.Server.Root.Raw})} {
		if os.WriteFile(filepath.Join(identity, name), data, 0600) != nil {
			t.Fatal("fixture identity")
		}
	}
	ports := []string{}
	held := []net.Listener{}
	for range 2 {
		listener, e := net.Listen("tcp4", "127.0.0.1:0")
		if e != nil {
			t.Fatal("fixture port")
		}
		held = append(held, listener)
		ports = append(ports, listener.Addr().String())
	}
	for _, listener := range held {
		listener.Close()
	}
	config := serverhost.Config{Schema: 1, IdentityDirectory: identity, AuthorityDirectory: state, WitnessDirectory: witness, ControlAddress: ports[0], BackendAddress: ports[1]}
	body, _ := json.Marshal(config)
	path := filepath.Join(identity, "server.json")
	os.WriteFile(path, body, 0600)
	owner := filepath.Join(identity, "owner-cert.pem")
	os.WriteFile(owner, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: f.Owner.Leaf.Raw}), 0600)
	return config, f, path, owner
}
func start(t *testing.T, config serverhost.Config) (context.CancelFunc, <-chan error) {
	t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	writer := &readyWriter{ready: make(chan struct{})}
	done := make(chan error, 1)
	go func() { done <- serve(ctx, config, writer) }()
	select {
	case <-writer.ready:
	case e := <-done:
		cancel()
		t.Fatalf("server ended before readiness: %s", code(e))
	case <-time.After(5 * time.Second):
		cancel()
		t.Fatal("readiness deadline")
	}
	t.Cleanup(cancel)
	return cancel, done
}
func stopped(t *testing.T, done <-chan error, success bool) {
	t.Helper()
	select {
	case e := <-done:
		if (e == nil) != success {
			t.Fatal("unexpected server exit category")
		}
	case <-time.After(5 * time.Second):
		t.Fatal("owned server did not stop")
	}
}
func closed(t *testing.T, config serverhost.Config) {
	t.Helper()
	for _, address := range []string{config.ControlAddress, config.BackendAddress} {
		socket, e := net.DialTimeout("tcp4", address, 200*time.Millisecond)
		if e == nil {
			socket.Close()
			t.Fatal("listener remained after server exit")
		}
	}
}
func TestServerExplicitInitControlAndShutdown(t *testing.T) {
	config, f, path, owner := serverFixture(t)
	var output bytes.Buffer
	if execute(context.Background(), []string{"init", path, owner}, &output) != nil || output.String() != "{\"initialized\":true}\n" {
		t.Fatal("explicit initialization")
	}
	cancel, done := start(t, config)
	tlsConfig, e := f.Owner.ClientTLS(f.Server.ID, time.Now)
	if e != nil {
		t.Fatal("fixture owner TLS")
	}
	transport := &http.Transport{TLSClientConfig: tlsConfig, DisableKeepAlives: true}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, Timeout: 3 * time.Second}
	request := []byte(`{"kind":"request","apiVersion":1,"requestId":"` + uuid.NewString() + `","method":"hello","params":{}}`)
	response, e := client.Post("https://"+config.ControlAddress+"/control/v1", "application/json", bytes.NewReader(request))
	if e != nil {
		t.Fatal("actual owner control handshake")
	}
	body, e := io.ReadAll(io.LimitReader(response.Body, 8192))
	response.Body.Close()
	if e != nil || !bytes.Contains(body, []byte(f.Server.ID)) {
		t.Fatal("authenticated server identity result")
	}
	unknownTLS, _ := f.Visitor.ClientTLS(f.Server.ID, time.Now)
	unknownTransport := &http.Transport{TLSClientConfig: unknownTLS, DisableKeepAlives: true}
	defer unknownTransport.CloseIdleConnections()
	unknown := &http.Client{Transport: unknownTransport, Timeout: 3 * time.Second}
	if rejected, e := unknown.Post("https://"+config.ControlAddress+"/control/v1", "application/json", bytes.NewReader(request)); e == nil {
		rejected.Body.Close()
		t.Fatal("unpaired fixture device inherited authority")
	}
	raw, e := net.DialTimeout("tcp4", config.BackendAddress, time.Second)
	if e != nil {
		t.Fatal("owned backend listener")
	}
	raw.SetDeadline(time.Now().Add(3 * time.Second))
	raw.Write([]byte("legacy-frp-token"))
	var one [1]byte
	if n, e := raw.Read(one[:]); n != 0 || e == nil {
		t.Fatal("unauthenticated backend accepted legacy input")
	}
	raw.Close()
	probeTLS, _ := f.Owner.ClientTLS(f.Server.ID, time.Now)
	probeTLS.NextProtos = []string{"linkgate-frp/1"}
	probe, e := tls.DialWithDialer(&net.Dialer{Timeout: 2 * time.Second}, "tcp4", config.BackendAddress, probeTLS)
	if e != nil {
		t.Fatal("actual protected backend TLS")
	}
	probe.Close()
	cancel()
	stopped(t, done, true)
	closed(t, config)
	host, e := serverhost.Open(config)
	if e != nil {
		t.Fatal("persistent state unlock on shutdown")
	}
	host.Close()
}
func TestServerStopsAfterProtectedStorageChange(t *testing.T) {
	config, _, path, owner := serverFixture(t)
	if execute(context.Background(), []string{"init", path, owner}, io.Discard) != nil {
		t.Fatal("initialize")
	}
	_, done := start(t, config)
	if os.Chmod(filepath.Join(config.WitnessDirectory, "witness.db"), 0644) != nil {
		t.Fatal("fixture storage mode")
	}
	stopped(t, done, false)
	closed(t, config)
}
func TestServerDoesNotBootstrapDuringServe(t *testing.T) {
	config, _, path, _ := serverFixture(t)
	if execute(context.Background(), []string{"serve", path}, io.Discard) == nil {
		t.Fatal("ordinary serve bootstrapped")
	}
	if _, e := os.Stat(filepath.Join(config.AuthorityDirectory, "authority.db")); !os.IsNotExist(e) {
		t.Fatal("serve created authority state")
	}
}
func TestServerFixedErrorsAndArguments(t *testing.T) {
	for _, args := range [][]string{nil, {"serve"}, {"unknown", "private-marker"}, {"serve", "private-marker", "extra"}, {"init", "private-marker"}} {
		e := execute(context.Background(), args, io.Discard)
		if e == nil || strings.Contains(code(e), "private-marker") {
			t.Fatal("invalid command reflected input")
		}
	}
	if code(io.ErrUnexpectedEOF) != "SERVER_FAILED" {
		t.Fatal("raw external failure escaped")
	}
}
func TestServerExplicitRequestKeepsPrivateStateLocal(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("protected request command requires Linux")
	}
	directory := t.TempDir()
	var output bytes.Buffer
	if os.Chmod(directory, 0700) != nil {
		t.Fatal("private fixture directory")
	}
	if execute(context.Background(), []string{"request", directory}, &output) != nil || output.String() != "{\"created\":true}\n" {
		t.Fatal("explicit request command")
	}
	if execute(context.Background(), []string{"request", directory}, io.Discard) == nil {
		t.Fatal("repeated request rotated identity")
	}
	entries, e := os.ReadDir(directory)
	if e != nil || len(entries) != 2 {
		t.Fatal("request changed unrelated state")
	}
}
