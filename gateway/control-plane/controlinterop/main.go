package main

import (
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

	"github.com/google/uuid"
	a "github.com/stardust2716/Redesk/gateway/control-plane/authority"
	bolt "go.etcd.io/bbolt"
)

func main() {
	if run() != nil {
		fmt.Fprintln(os.Stderr, "Actual control fixture failed.")
		os.Exit(1)
	}
}

func run() error {
	if len(os.Args) != 2 {
		return fmt.Errorf("directory required")
	}
	directory, err := filepath.Abs(os.Args[1])
	if err != nil {
		return err
	}
	info, err := os.Lstat(directory)
	if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
		return fmt.Errorf("protected directory required")
	}
	write := func(name string, data []byte) error {
		target := filepath.Join(directory, name)
		if _, e := os.Lstat(target); !os.IsNotExist(e) {
			return fmt.Errorf("fixture output already exists")
		}
		pending := target + ".part"
		f, e := os.OpenFile(pending, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
		if e != nil {
			return e
		}
		defer f.Close()
		if _, e = f.Write(data); e != nil {
			return e
		}
		if e = f.Sync(); e != nil {
			return e
		}
		if e = f.Close(); e != nil {
			return e
		}
		return os.Rename(pending, target)
	}
	now := time.Now()
	rootKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return err
	}
	rootTemplate := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "Control integration owner"}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(24 * time.Hour), IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign | x509.KeyUsageCRLSign}
	rootDER, err := x509.CreateCertificate(rand.Reader, rootTemplate, rootTemplate, &rootKey.PublicKey, rootKey)
	if err != nil {
		return err
	}
	root, err := x509.ParseCertificate(rootDER)
	if err != nil {
		return err
	}
	if err = write("owner-root.pem", pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: rootDER})); err != nil {
		return err
	}
	sign := func(public any, serial int64) ([]byte, error) {
		return x509.CreateCertificate(rand.Reader, &x509.Certificate{SerialNumber: big.NewInt(serial), Subject: pkix.Name{CommonName: a.DeviceName}, DNSNames: []string{a.DeviceName}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(4 * time.Hour), BasicConstraintsValid: true, KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth, x509.ExtKeyUsageClientAuth}}, root, public, rootKey)
	}
	serverKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return err
	}
	serverDER, err := sign(&serverKey.PublicKey, 2)
	if err != nil {
		return err
	}
	identity, err := a.NewDeviceIdentity(tls.Certificate{Certificate: [][]byte{serverDER, rootDER}, PrivateKey: serverKey}, root, now)
	if err != nil {
		return err
	}
	if err = write("server-cert.pem", pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: serverDER})); err != nil {
		return err
	}
	ids := make(map[string]string)
	for index, name := range []string{"owner", "publisher", "visitor", "java"} {
		encoded, e := os.ReadFile(filepath.Join(directory, name+".csr"))
		if e != nil || len(encoded) > 4096 {
			return fmt.Errorf("CSR unavailable")
		}
		block, rest := pem.Decode(encoded)
		if block == nil || len(rest) != 0 || block.Type != "CERTIFICATE REQUEST" {
			return fmt.Errorf("CSR malformed")
		}
		request, e := x509.ParseCertificateRequest(block.Bytes)
		if e != nil || request.CheckSignature() != nil {
			return fmt.Errorf("CSR invalid")
		}
		public, ok := request.PublicKey.(*ecdsa.PublicKey)
		if !ok {
			return fmt.Errorf("CSR key invalid")
		}
		id, e := a.IssuerID(public)
		if e != nil {
			return e
		}
		ids[name] = id
		cert, e := sign(public, int64(index+3))
		if e != nil {
			return e
		}
		if e = write(name+"-cert.pem", pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: cert})); e != nil {
			return e
		}
	}
	db, err := bolt.Open(filepath.Join(directory, "authority.db"), 0600, &bolt.Options{Timeout: time.Second})
	if err != nil {
		return err
	}
	defer db.Close()
	wdb, err := bolt.Open(filepath.Join(directory, "witness.db"), 0600, &bolt.Options{Timeout: time.Second})
	if err != nil {
		return err
	}
	defer wdb.Close()
	witness, err := a.NewBoltWitness(wdb, true)
	if err != nil {
		return err
	}
	digest := sha256.Sum256(rootDER)
	store, err := a.NewStore(db, witness, identity.ID, hex.EncodeToString(digest[:]), ids["owner"], now, true)
	if err != nil {
		return err
	}
	for _, name := range []string{"publisher", "visitor", "java"} {
		if _, err = store.SetDevice(ids["owner"], uuid.NewString(), ids[name], 0, a.Roles{Publish: name == "publisher", Access: name != "publisher"}, true, now); err != nil {
			return err
		}
	}

	for _, service := range []struct{ name, protocol, authority string }{{"files", "https", "files.example.invalid"}, {"stream", "tcp", ""}} {
		target := a.ServiceScope{PublisherDevice: ids["publisher"], PublisherApp: "org.example.publisher", Service: service.name}
		if _, err = store.SetService(ids["owner"], uuid.NewString(), target, 0, service.protocol, service.authority, true, now); err != nil {
			return err
		}
		for _, name := range []string{"visitor", "java"} {
			if _, err = store.SetGrant(ids["owner"], uuid.NewString(), a.AccessScope{VisitorDevice: ids[name], VisitorApp: "org.example.client", Target: target}, 0, true, now); err != nil {
				return err
			}
		}
	}

	var server *a.ControlServer
	var listener net.Listener
	var finished chan error
	start := func(port int) error {
		server, err = a.NewControlServer(identity, store, time.Now)
		if err != nil {
			return err
		}
		listener, err = net.Listen("tcp4", fmt.Sprintf("127.0.0.1:%d", port))
		if err != nil {
			return err
		}
		finished = make(chan error, 1)
		active, done, owned := server, finished, listener
		go func() { done <- active.Serve(owned) }()
		return nil
	}
	if err = start(0); err != nil {
		return err
	}
	defer func() {
		if server != nil {
			server.Close()
		}
		if listener != nil {
			listener.Close()
		}
	}()
	configuration := struct {
		Port      int    `json:"port"`
		Publisher string `json:"publisher"`
		Visitor   string `json:"visitor"`
		Java      string `json:"java"`
	}{listener.Addr().(*net.TCPAddr).Port, ids["publisher"], ids["visitor"], ids["java"]}
	encoded, err := json.Marshal(configuration)
	if err != nil {
		return err
	}
	if err = write("ready.json", encoded); err != nil {
		return err
	}
	deadline := time.NewTimer(120 * time.Second)
	defer deadline.Stop()
	tick := time.NewTicker(20 * time.Millisecond)
	defer tick.Stop()
	revoked, approved, restarted := false, false, false
	for {
		select {
		case <-deadline.C:
			return nil
		case <-finished:
			return fmt.Errorf("control listener exited early")
		case <-tick.C:
			if _, e := os.Stat(filepath.Join(directory, "stop")); e == nil {
				if err = write("stopped", []byte("true")); err != nil {
					return err
				}
				return nil
			}

			for _, label := range []string{"before", "after"} {
				marker := filepath.Join(directory, "observe-"+label)
				if _, e := os.Stat(marker); e == nil {
					if _, e = os.Stat(filepath.Join(directory, "observation-"+label+".json")); os.IsNotExist(e) {
						value, e := json.Marshal(server.Statistics())
						if e != nil {
							return e
						}
						if e = write("observation-"+label+".json", value); e != nil {
							return e
						}
					}
				}
			}
			if revoked && !approved {
				if _, e := os.Stat(filepath.Join(directory, "approve")); e == nil {
					for _, service := range []string{"files", "stream"} {
						for _, name := range []string{"visitor", "java"} {
							target := a.ServiceScope{PublisherDevice: ids["publisher"], PublisherApp: "org.example.publisher", Service: service}
							if _, err = store.SetGrant(ids["owner"], uuid.NewString(), a.AccessScope{VisitorDevice: ids[name], VisitorApp: "org.example.client", Target: target}, 2, true, time.Now()); err != nil {
								return err
							}
						}
					}
					if err = write("approved", []byte("true")); err != nil {
						return err
					}
					approved = true
				}
			}
			if approved && !restarted {
				if _, e := os.Stat(filepath.Join(directory, "restart")); e == nil {
					port := listener.Addr().(*net.TCPAddr).Port
					server.Close()
					listener.Close()
					<-finished
					if err = start(port); err != nil {
						return err
					}
					if err = write("restarted", []byte("true")); err != nil {
						return err
					}
					restarted = true
				}
			}
			if !revoked {
				if _, e := os.Stat(filepath.Join(directory, "revoke")); e == nil {
					for _, service := range []string{"files", "stream"} {
						for _, name := range []string{"visitor", "java"} {
							target := a.ServiceScope{PublisherDevice: ids["publisher"], PublisherApp: "org.example.publisher", Service: service}
							if _, err = store.SetGrant(ids["owner"], uuid.NewString(), a.AccessScope{VisitorDevice: ids[name], VisitorApp: "org.example.client", Target: target}, 1, false, time.Now()); err != nil {
								return err
							}
						}
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
