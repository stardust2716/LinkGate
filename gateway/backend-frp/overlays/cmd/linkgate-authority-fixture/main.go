package main

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
	"encoding/json/v2"
	"encoding/pem"
	"fmt"
	"math/big"
	"net"
	"os"
	"path/filepath"
	"time"

	v1 "github.com/fatedier/frp/pkg/config/v1"
	"github.com/fatedier/frp/pkg/util/log"
	"github.com/fatedier/frp/server"
	"github.com/google/uuid"
	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
	bolt "go.etcd.io/bbolt"
)

func main() {
	if run() != nil {
		fmt.Fprintln(os.Stderr, "Native runtime authority fixture failed.")
		os.Exit(1)
	}
}
func run() error {
	if len(os.Args) != 2 {
		return fmt.Errorf("fixture directory required")
	}
	directory, err := filepath.Abs(os.Args[1])
	if err != nil {
		return err
	}
	info, err := os.Lstat(directory)
	if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
		return fmt.Errorf("protected fixture directory required")
	}
	write := func(name string, data []byte) error {
		target := filepath.Join(directory, name)
		if _, e := os.Lstat(target); !os.IsNotExist(e) {
			return fmt.Errorf("fixture output exists")
		}
		file, e := os.OpenFile(target+".part", os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
		if e != nil {
			return e
		}
		defer file.Close()
		if _, e = file.Write(data); e != nil {
			return e
		}
		if e = file.Sync(); e != nil {
			return e
		}
		if e = file.Close(); e != nil {
			return e
		}
		return os.Rename(target+".part", target)
	}
	now := time.Now()
	rootKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return err
	}
	serverKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return err
	}
	rootTemplate := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "Native backend fixture owner"}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(24 * time.Hour), IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign | x509.KeyUsageCRLSign}
	rootDER, err := x509.CreateCertificate(rand.Reader, rootTemplate, rootTemplate, &rootKey.PublicKey, rootKey)
	if err != nil {
		return err
	}
	root, err := x509.ParseCertificate(rootDER)
	if err != nil {
		return err
	}
	sign := func(public any, serial int64) ([]byte, error) {
		return x509.CreateCertificate(rand.Reader, &x509.Certificate{SerialNumber: big.NewInt(serial), Subject: pkix.Name{CommonName: authority.DeviceName}, DNSNames: []string{authority.DeviceName}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(time.Hour), BasicConstraintsValid: true, KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth, x509.ExtKeyUsageServerAuth}}, root, public, rootKey)
	}
	serverDER, err := sign(&serverKey.PublicKey, 2)
	if err != nil {
		return err
	}
	identity, err := authority.NewDeviceIdentity(tls.Certificate{Certificate: [][]byte{serverDER, rootDER}, PrivateKey: serverKey}, root, now)
	if err != nil {
		return err
	}
	ids := make(map[string]string)
	clients := []string{"publisher", "visitor"}
	if _, e := os.Stat(filepath.Join(directory, "android.csr")); e == nil {
		clients = append(clients, "android")
	} else if !os.IsNotExist(e) {
		return e
	}
	for index, name := range clients {
		bytes, e := os.ReadFile(filepath.Join(directory, name+".csr"))
		if e != nil || len(bytes) > 4096 {
			return fmt.Errorf("public CSR unavailable")
		}
		block, rest := pem.Decode(bytes)
		if block == nil || len(rest) != 0 || block.Type != "CERTIFICATE REQUEST" {
			return fmt.Errorf("public CSR malformed")
		}
		request, e := x509.ParseCertificateRequest(block.Bytes)
		if e != nil || request.CheckSignature() != nil {
			return fmt.Errorf("public CSR invalid")
		}
		key, ok := request.PublicKey.(*ecdsa.PublicKey)
		if !ok {
			return fmt.Errorf("public CSR key invalid")
		}
		ids[name], e = authority.IssuerID(key)
		if e != nil {
			return e
		}
		certificate, e := sign(key, int64(index+3))
		if e != nil {
			return e
		}
		if e = write(name+"-cert.pem", pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: certificate})); e != nil {
			return e
		}
	}
	for name, data := range map[string][]byte{"owner-root.pem": rootDER, "server-cert.pem": serverDER} {
		if err = write(name, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: data})); err != nil {
			return err
		}
	}
	db, err := bolt.Open(filepath.Join(directory, "authority.db"), 0600, nil)
	if err != nil {
		return err
	}
	defer db.Close()
	wdb, err := bolt.Open(filepath.Join(directory, "witness.db"), 0600, nil)
	if err != nil {
		return err
	}
	defer wdb.Close()
	witness, err := authority.NewBoltWitness(wdb, true)
	if err != nil {
		return err
	}
	digest := sha256.Sum256(rootDER)
	store, err := authority.NewStore(db, witness, identity.ID, hex.EncodeToString(digest[:]), ids["publisher"], now, true)
	if err != nil {
		return err
	}
	if _, err = store.SetDevice(ids["publisher"], uuid.NewString(), ids["publisher"], 1, authority.Roles{Owner: true, Publish: true}, true, now); err != nil {
		return err
	}
	if _, err = store.SetDevice(ids["publisher"], uuid.NewString(), ids["visitor"], 0, authority.Roles{Access: true}, true, now); err != nil {
		return err
	}
	if ids["android"] != "" {
		if _, err = store.SetDevice(ids["publisher"], uuid.NewString(), ids["android"], 0, authority.Roles{Access: true}, true, now); err != nil {
			return err
		}
	}
	target := authority.ServiceScope{PublisherDevice: ids["publisher"], PublisherApp: "org.example.publisher", Service: "stream"}
	scope := authority.AccessScope{VisitorDevice: ids["visitor"], VisitorApp: "org.example.client", Target: target}
	if _, err = store.SetService(ids["publisher"], uuid.NewString(), target, 0, "tcp", "", true, now); err != nil {
		return err
	}
	if _, err = store.SetGrant(ids["publisher"], uuid.NewString(), scope, 0, true, now); err != nil {
		return err
	}
	androidSdkScope := authority.AccessScope{VisitorDevice: ids["android"], VisitorApp: "org.example.native-consumer", Target: target}
	androidScope := authority.AccessScope{VisitorDevice: ids["android"], VisitorApp: "org.example.client", Target: target}
	if ids["android"] != "" {
		if _, err = store.SetGrant(ids["publisher"], uuid.NewString(), androidSdkScope, 0, true, now); err != nil {
			return err
		}
		if _, err = store.SetGrant(ids["publisher"], uuid.NewString(), androidScope, 0, true, now); err != nil {
			return err
		}
	}
	control, err := authority.NewControlServer(identity, store, time.Now)
	if err != nil {
		return err
	}
	defer control.Close()
	httpListener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return err
	}
	defer httpListener.Close()
	go func() { _ = control.Serve(httpListener) }()
	gate, err := authority.NewTransportGate(identity, store, time.Now)
	if err != nil {
		return err
	}
	defer gate.Close()
	stunFixture, err := newLoopbackSTUN()
	if err != nil {
		return err
	}
	defer stunFixture.Close()
	cfg := &v1.ServerConfig{}
	if err = cfg.Complete(); err != nil {
		return err
	}
	cfg.BindAddr = "127.0.0.1"
	cfg.BindPort = 0
	cfg.ProxyBindAddr = "127.0.0.1"
	cfg.Transport.HeartbeatTimeout = 3
	log.InitLogger("console", "error", 1, true)
	backend, err := server.NewAuthenticatedService(cfg, gate)
	if err != nil {
		return err
	}
	defer backend.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go backend.Run(ctx)
	configuration := struct {
		ControlPort    int    `json:"controlPort"`
		BackendPort    int    `json:"backendPort"`
		STUNPort       int    `json:"stunPort"`
		STUNSecondPort int    `json:"stunSecondPort"`
		Server         string `json:"server"`
		Publisher      string `json:"publisher"`
		Visitor        string `json:"visitor"`
	}{httpListener.Addr().(*net.TCPAddr).Port, backend.AuthenticatedAddr().(*net.TCPAddr).Port, stunFixture.Port(), stunFixture.SecondPort(), identity.ID, ids["publisher"], ids["visitor"]}
	encoded, err := json.Marshal(configuration)
	if err != nil {
		return err
	}
	if err = write("ready.json", encoded); err != nil {
		return err
	}
	tick := time.NewTicker(50 * time.Millisecond)
	defer tick.Stop()
	timeout := time.NewTimer(180 * time.Second)
	defer timeout.Stop()
	published, controlled, observed, directObserved, revoked, androidRevoked, androidSdkRevoked := false, false, false, false, false, false, false
	for {
		select {
		case <-timeout.C:
			return nil
		case <-tick.C:
			if !androidRevoked && ids["android"] != "" {
				if _, e := os.Stat(filepath.Join(directory, "revoke-android")); e == nil {
					if _, err = store.SetGrant(ids["publisher"], uuid.NewString(), androidScope, 1, false, time.Now()); err != nil {
						return err
					}
					if err = write("android-revoked", []byte("true")); err != nil {
						return err
					}
					androidRevoked = true
				}
			}
			if !androidSdkRevoked && ids["android"] != "" {
				if _, e := os.Stat(filepath.Join(directory, "revoke-android-sdk")); e == nil {
					if _, err = store.SetGrant(ids["publisher"], uuid.NewString(), androidSdkScope, 1, false, time.Now()); err != nil {
						return err
					}
					if err = write("android-sdk-revoked", []byte("true")); err != nil {
						return err
					}
					androidSdkRevoked = true
				}
			}
			if !directObserved {
				if _, err := os.Stat(filepath.Join(directory, "observe-direct")); err == nil {
					encoded, e := json.Marshal(stunFixture.Observations())
					if e != nil {
						return e
					}
					if err = write("direct-observation.json", encoded); err != nil {
						return err
					}
					directObserved = true
				}
			}
			if _, e := os.Stat(filepath.Join(directory, "stop")); e == nil {
				return nil
			}
			statistics := gate.Statistics()
			if !published && statistics.Publications >= 1 {
				value, e := json.Marshal(statistics)
				if e != nil {
					return e
				}
				if e = write("published.json", value); e != nil {
					return e
				}
				published = true
			}
			if !controlled && statistics.Controls >= 2 {
				value, e := json.Marshal(statistics)
				if e != nil {
					return e
				}
				if e = write("controlled.json", value); e != nil {
					return e
				}
				controlled = true
			}
			if !observed {
				if _, e := os.Stat(filepath.Join(directory, "observe")); e == nil {
					value, e := json.Marshal(statistics)
					if e != nil {
						return e
					}
					if e = write("observation.json", value); e != nil {
						return e
					}
					observed = true
				}
			}
			if !revoked {
				if _, e := os.Stat(filepath.Join(directory, "revoke")); e == nil {
					if _, err = store.SetGrant(ids["publisher"], uuid.NewString(), scope, 1, false, time.Now()); err != nil {
						return err
					}
					if err = write("revoked", []byte("true")); err != nil {
						return err
					}
					revoked = true
				}
			}
		}
	}
}
