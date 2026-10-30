package authority

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"io"
	"math/big"
	"net"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/uuid"
)

func rootFixture(t *testing.T) (*x509.Certificate, *ecdsa.PrivateKey) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now()
	template := &x509.Certificate{SerialNumber: serial, Subject: pkix.Name{CommonName: "LinkGate fixture root"}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(72 * time.Hour), IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign | x509.KeyUsageCRLSign}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	certificate, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatal(err)
	}
	return certificate, key
}
func deviceFixture(t *testing.T, root *x509.Certificate, signer *ecdsa.PrivateKey) *DeviceIdentity {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now()
	template := &x509.Certificate{SerialNumber: serial, Subject: pkix.Name{CommonName: DeviceName}, DNSNames: []string{DeviceName}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(24 * time.Hour), BasicConstraintsValid: true, KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth, x509.ExtKeyUsageServerAuth}}
	der, err := x509.CreateCertificate(rand.Reader, template, root, &key.PublicKey, signer)
	if err != nil {
		t.Fatal(err)
	}
	identity, err := NewDeviceIdentity(tls.Certificate{Certificate: [][]byte{der, root.Raw}, PrivateKey: key}, root, now)
	if err != nil {
		t.Fatal(err)
	}
	return identity
}
func TestActualMutualTlsBindsDeviceBeforeApplicationBytes(t *testing.T) {
	root, rootKey := rootFixture(t)
	server := deviceFixture(t, root, rootKey)
	client := deviceFixture(t, root, rootKey)
	unknown := deviceFixture(t, root, rootKey)
	otherRoot, otherRootKey := rootFixture(t)
	foreign := deviceFixture(t, otherRoot, otherRootKey)
	var admitted atomic.Bool
	admitted.Store(true)
	for _, scenario := range []struct {
		name     string
		identity *DeviceIdentity
		expected string
		allowed  bool
	}{
		{"approved", client, server.ID, true}, {"unapproved device", unknown, server.ID, false}, {"foreign owner root", foreign, server.ID, false}, {"substituted server key", client, unknown.ID, false},
	} {
		t.Run(scenario.name, func(t *testing.T) {
			cfg, err := server.ServerTLS(func(id string) bool { return admitted.Load() && id == client.ID }, time.Now)
			if err != nil {
				t.Fatal(err)
			}
			listener, err := tls.Listen("tcp", "127.0.0.1:0", cfg)
			if err != nil {
				t.Fatal(err)
			}
			defer listener.Close()
			done := make(chan struct{})
			var delivered atomic.Int32
			var authenticated string
			go func() {
				defer close(done)
				connection, err := listener.Accept()
				if err != nil {
					return
				}
				defer connection.Close()
				_ = connection.SetDeadline(time.Now().Add(3 * time.Second))
				secure := connection.(*tls.Conn)
				if secure.Handshake() != nil {
					return
				}
				authenticated, err = HandshakeDevice(secure.ConnectionState())
				if err != nil {
					return
				}
				var payload [1]byte
				if _, err = io.ReadFull(secure, payload[:]); err != nil {
					return
				}
				delivered.Add(1)
				_, _ = secure.Write(payload[:])
			}()
			clientCfg, err := scenario.identity.ClientTLS(scenario.expected, time.Now)
			if err != nil {
				t.Fatal(err)
			}
			dialer := &net.Dialer{Timeout: 3 * time.Second}
			connection, err := tls.DialWithDialer(dialer, "tcp", listener.Addr().String(), clientCfg)
			if err == nil {
				_ = connection.SetDeadline(time.Now().Add(3 * time.Second))
				_, err = connection.Write([]byte{42})
				if err == nil {
					var response [1]byte
					_, err = io.ReadFull(connection, response[:])
					if err == nil && response[0] != 42 {
						t.Fatal("application bytes changed")
					}
				}
				connection.Close()
			}
			<-done
			if scenario.allowed {
				if err != nil || delivered.Load() != 1 || authenticated != client.ID {
					t.Fatalf("approved handshake failed: %v", err)
				}
			} else if err == nil || delivered.Load() != 0 {
				t.Fatal("unapproved handshake delivered application bytes")
			}
		})
	}
	admitted.Store(false)
	cfg, err := server.ServerTLS(func(id string) bool { return admitted.Load() && id == client.ID }, time.Now)
	if err != nil || !cfg.SessionTicketsDisabled || cfg.MinVersion < tls.VersionTLS12 {
		t.Fatal("unsafe TLS policy")
	}
}
func TestDeviceProfileAndPrivateKeyMatch(t *testing.T) {
	root, key := rootFixture(t)
	identity := deviceFixture(t, root, key)
	wrong := deviceFixture(t, root, key)
	mutated := identity.Certificate
	mutated.PrivateKey = wrong.Certificate.PrivateKey
	if _, err := NewDeviceIdentity(mutated, root, time.Now()); err == nil {
		t.Fatal("wrong local private key accepted")
	}
	if _, err := identity.ClientTLS(identity.ID, func() time.Time { return time.Now().Add(25 * time.Hour) }); err == nil {
		t.Fatal("expired local device accepted")
	}
	if _, err := HandshakeDevice(tls.ConnectionState{}); err == nil {
		t.Fatal("unverified connection state accepted")
	}
}
func TestServerRoleChecksIgnoreClaimedUserIdentity(t *testing.T) {
	f := newStoreFixture(t)
	if _, err := f.s.AdmitDevice(f.visitor, "access", f.now); err != nil {
		t.Fatal(err)
	}
	_, err := f.s.AdmitDevice(f.visitor, "publish", f.now)
	wantCode(t, err, "AUTH_REQUIRED")
	_, err = f.s.AdmitDevice(f.publisher, "owner", f.now)
	wantCode(t, err, "AUTH_REQUIRED")
	if _, err = f.s.SetDevice(f.owner, uuid.NewString(), f.visitor, 1, Roles{Access: true}, false, f.now); err != nil {
		t.Fatal(err)
	}
	_, err = f.s.AdmitDevice(f.visitor, "paired", f.now)
	wantCode(t, err, "AUTH_REQUIRED")
}
