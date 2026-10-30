package linkgatetest

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"math/big"
	"net"
	"path/filepath"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
	bolt "go.etcd.io/bbolt"
)

type Fixture struct {
	Store                                                           *authority.Store
	Gate                                                            *authority.TransportGate
	Server, Owner, Publisher, Visitor, OtherPublisher, OtherVisitor *authority.DeviceIdentity
	Target                                                          authority.ServiceScope
	Scope                                                           authority.AccessScope
}

func New(t *testing.T) *Fixture {
	t.Helper()
	now := time.Now()
	rootKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal("fixture root generation failed")
	}
	rootTemplate := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "Backend test owner"}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(24 * time.Hour), IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign | x509.KeyUsageCRLSign}
	rootDER, err := x509.CreateCertificate(rand.Reader, rootTemplate, rootTemplate, &rootKey.PublicKey, rootKey)
	if err != nil {
		t.Fatal("fixture root signing failed")
	}
	root, err := x509.ParseCertificate(rootDER)
	if err != nil {
		t.Fatal("fixture root parsing failed")
	}
	serial := int64(2)
	device := func() *authority.DeviceIdentity {
		key, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
		if e != nil {
			t.Fatal("fixture device generation failed")
		}
		serial++
		leaf, e := x509.CreateCertificate(rand.Reader, &x509.Certificate{SerialNumber: big.NewInt(serial), Subject: pkix.Name{CommonName: authority.DeviceName}, DNSNames: []string{authority.DeviceName}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(time.Hour), BasicConstraintsValid: true, KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth, x509.ExtKeyUsageClientAuth}}, root, &key.PublicKey, rootKey)
		if e != nil {
			t.Fatal("fixture device signing failed")
		}
		identity, e := authority.NewDeviceIdentity(tls.Certificate{Certificate: [][]byte{leaf, rootDER}, PrivateKey: key}, root, now)
		if e != nil {
			t.Fatal("fixture device profile failed")
		}
		return identity
	}
	f := &Fixture{Server: device(), Owner: device(), Publisher: device(), Visitor: device(), OtherPublisher: device(), OtherVisitor: device()}
	directory := t.TempDir()
	db, err := bolt.Open(filepath.Join(directory, "authority.db"), 0600, nil)
	if err != nil {
		t.Fatal("fixture authority open failed")
	}
	t.Cleanup(func() { _ = db.Close() })
	wdb, err := bolt.Open(filepath.Join(directory, "witness.db"), 0600, nil)
	if err != nil {
		t.Fatal("fixture witness open failed")
	}
	t.Cleanup(func() { _ = wdb.Close() })
	witness, err := authority.NewBoltWitness(wdb, true)
	if err != nil {
		t.Fatal("fixture witness failed")
	}
	digest := sha256.Sum256(rootDER)
	f.Store, err = authority.NewStore(db, witness, f.Server.ID, hex.EncodeToString(digest[:]), f.Owner.ID, now, true)
	if err != nil {
		t.Fatal("fixture authority failed")
	}
	for _, entry := range []struct {
		device *authority.DeviceIdentity
		role   authority.Roles
	}{{f.Publisher, authority.Roles{Publish: true}}, {f.Visitor, authority.Roles{Access: true}}, {f.OtherPublisher, authority.Roles{Publish: true}}, {f.OtherVisitor, authority.Roles{Access: true}}} {
		if _, err = f.Store.SetDevice(f.Owner.ID, uuid.NewString(), entry.device.ID, 0, entry.role, true, now); err != nil {
			t.Fatal("fixture device approval failed")
		}
	}
	f.Target = authority.ServiceScope{PublisherDevice: f.Publisher.ID, PublisherApp: "org.example.publisher", Service: "files"}
	f.Scope = authority.AccessScope{VisitorDevice: f.Visitor.ID, VisitorApp: "org.example.client", Target: f.Target}
	for _, target := range []authority.ServiceScope{f.Target, {PublisherDevice: f.OtherPublisher.ID, PublisherApp: "org.example.publisher", Service: "files"}} {
		if _, err = f.Store.SetService(f.Owner.ID, uuid.NewString(), target, 0, "tcp", "", true, now); err != nil {
			t.Fatal("fixture service approval failed")
		}
	}
	if _, err = f.Store.SetGrant(f.Owner.ID, uuid.NewString(), f.Scope, 0, true, now); err != nil {
		t.Fatal("fixture grant approval failed")
	}
	f.Gate, err = authority.NewTransportGate(f.Server, f.Store, time.Now)
	if err != nil {
		t.Fatal("fixture transport gate failed")
	}
	t.Cleanup(f.Gate.Close)
	return f
}

func (f *Fixture) Dial(t *testing.T, address string, device *authority.DeviceIdentity, app, role string) net.Conn {
	t.Helper()
	cfg, err := device.ClientTLS(f.Server.ID, time.Now)
	if err != nil {
		t.Fatal("fixture client identity failed")
	}
	cfg.NextProtos = []string{authority.TransportALPN}
	connection, err := tls.DialWithDialer(&net.Dialer{Timeout: 2 * time.Second}, "tcp4", address, cfg)
	if err != nil {
		t.Fatal("actual fixture TLS handshake failed")
	}
	t.Cleanup(func() { _ = connection.Close() })
	_ = connection.SetDeadline(time.Now().Add(3 * time.Second))
	if authority.WriteTransportScope(connection, app, role) != nil {
		t.Fatal("actual fixture Agent scope failed")
	}
	_ = connection.SetDeadline(time.Time{})
	return connection
}

func (f *Fixture) Control(t *testing.T, device *authority.DeviceIdentity, app, role, run string) *authority.TransportControl {
	t.Helper()
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal("fixture transport listen failed")
	}
	defer listener.Close()
	type result struct {
		conn *authority.TransportConn
		err  error
	}
	accepted := make(chan result, 1)
	go func() {
		raw, e := listener.Accept()
		if e != nil {
			accepted <- result{nil, e}
			return
		}
		conn, e := f.Gate.Accept(context.Background(), raw)
		accepted <- result{conn, e}
	}()
	_ = f.Dial(t, listener.Addr().String(), device, app, role)
	var peer *authority.TransportPeer
	select {
	case value := <-accepted:
		if value.err != nil {
			t.Fatal("actual fixture scope rejected")
		}
		peer = value.conn.Peer()
	case <-time.After(5 * time.Second):
		t.Fatal("fixture transport accept stalled")
	}
	control, err := f.Gate.Login(peer, run, peer.Namespace())
	if err != nil {
		t.Fatal("fixture actual control login failed")
	}
	ctx, cancel := context.WithCancel(context.Background())
	finished := make(chan struct{})
	go func() {
		defer close(finished)
		ticker := time.NewTicker(500 * time.Millisecond)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-control.Done():
				return
			case <-ticker.C:
				_ = f.Gate.Heartbeat(control)
			}
		}
	}()
	t.Cleanup(func() { cancel(); control.Close(); <-finished })
	return control
}
