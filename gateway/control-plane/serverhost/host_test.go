package serverhost

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json/v2"
	"encoding/pem"
	"math/big"
	"os"
	"path/filepath"
	"runtime"
	"testing"
	"time"

	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
)

type fixture struct {
	config  Config
	owner   []byte
	ownerID string
	root    *x509.Certificate
	rootKey *ecdsa.PrivateKey
}

func provision(t *testing.T) fixture {
	t.Helper()
	if runtime.GOOS != "linux" {
		t.Skip("actual protected host lifecycle requires Linux")
	}
	directory := t.TempDir()
	paths := []string{filepath.Join(directory, "identity"), filepath.Join(directory, "authority"), filepath.Join(directory, "witness")}
	for _, path := range paths {
		if e := os.Mkdir(path, 0700); e != nil {
			t.Fatal("fixture directory")
		}
	}
	key := func() *ecdsa.PrivateKey {
		value, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
		if e != nil {
			t.Fatal("fixture key")
		}
		return value
	}
	rootKey := key()
	now := time.Now()
	template := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "Host fixture"}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(time.Hour), IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign}
	der, e := x509.CreateCertificate(rand.Reader, template, template, &rootKey.PublicKey, rootKey)
	if e != nil {
		t.Fatal("fixture root")
	}
	root, e := x509.ParseCertificate(der)
	if e != nil {
		t.Fatal("fixture root profile")
	}
	sign := func(public any, serial int64) []byte {
		leaf, e := x509.CreateCertificate(rand.Reader, &x509.Certificate{SerialNumber: big.NewInt(serial), DNSNames: []string{authority.DeviceName}, NotBefore: now.Add(-time.Minute), NotAfter: now.Add(30 * time.Minute), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth, x509.ExtKeyUsageServerAuth}}, root, public, rootKey)
		if e != nil {
			t.Fatal("fixture leaf")
		}
		return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: leaf})
	}
	serverKey, ownerKey := key(), key()
	private, e := x509.MarshalPKCS8PrivateKey(serverKey)
	if e != nil {
		t.Fatal("fixture key encoding")
	}
	for name, value := range map[string][]byte{"device-key.pem": pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: private}), "device-cert.pem": sign(&serverKey.PublicKey, 2), "owner-root.pem": pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})} {
		if os.WriteFile(filepath.Join(paths[0], name), value, 0600) != nil {
			t.Fatal("fixture identity write")
		}
	}
	ownerID, e := authority.IssuerID(&ownerKey.PublicKey)
	if e != nil {
		t.Fatal("fixture owner")
	}
	return fixture{Config{1, paths[0], paths[1], paths[2], "127.0.0.1:23451", "127.0.0.1:23452"}, sign(&ownerKey.PublicKey, 3), ownerID, root, rootKey}
}
func TestProtectedInitializeAndExistingOpen(t *testing.T) {
	f := provision(t)
	if Initialize(f.config, f.owner) != nil {
		t.Fatal("initialization")
	}
	if Initialize(f.config, f.owner) == nil {
		t.Fatal("existing state overwritten")
	}
	host, e := Open(f.config)
	if e != nil {
		t.Fatal("normal open")
	}
	defer host.Close()
	if _, e = host.Store.AdmitDevice(f.ownerID, "owner", time.Now()); e != nil {
		t.Fatal("bootstrap owner not admitted")
	}
	if duplicate, e := Open(f.config); e == nil {
		duplicate.Close()
		t.Fatal("concurrent host acquired same databases")
	}
	if host.Check() != nil {
		t.Fatal("unchanged identity check")
	}
	if host.Close() != nil {
		t.Fatal("close")
	}
	again, e := Open(f.config)
	if e != nil {
		t.Fatal("restart existing state")
	}
	again.Close()
}
func TestMissingWitnessIsNotRecreated(t *testing.T) {
	f := provision(t)
	if Initialize(f.config, f.owner) != nil {
		t.Fatal("initialize")
	}
	path := filepath.Join(f.config.WitnessDirectory, "witness.db")
	if os.Remove(path) != nil {
		t.Fatal("fixture remove")
	}
	if host, e := Open(f.config); e == nil {
		host.Close()
		t.Fatal("missing witness repaired")
	}
	if _, e := os.Lstat(path); !os.IsNotExist(e) {
		t.Fatal("missing witness was recreated")
	}
}
func TestInvalidOwnerCannotCreateState(t *testing.T) {
	f := provision(t)
	if Initialize(f.config, []byte("not a certificate")) != ErrIdentity {
		t.Fatal("invalid owner accepted")
	}
	if _, e := os.Stat(filepath.Join(f.config.AuthorityDirectory, "authority.db")); !os.IsNotExist(e) {
		t.Fatal("state created before owner authentication")
	}
	other := provision(t)
	if Initialize(f.config, other.owner) != ErrIdentity {
		t.Fatal("foreign owner root accepted")
	}
}
func TestPrivateModesHardlinksAndAncestors(t *testing.T) {
	f := provision(t)
	path := filepath.Join(f.config.IdentityDirectory, "device-key.pem")
	if os.Chmod(path, 0644) != nil {
		t.Fatal("fixture chmod")
	}
	if Initialize(f.config, f.owner) == nil {
		t.Fatal("readable key accepted")
	}
	os.Chmod(path, 0600)
	link := filepath.Join(f.config.IdentityDirectory, "duplicate-key")
	if os.Link(path, link) != nil {
		t.Fatal("fixture hardlink")
	}
	if Initialize(f.config, f.owner) == nil {
		t.Fatal("hardlinked key accepted")
	}
	os.Remove(link)
	if os.Chmod(f.config.WitnessDirectory, 0770) != nil {
		t.Fatal("fixture chmod")
	}
	if Initialize(f.config, f.owner) == nil {
		t.Fatal("shared witness directory accepted")
	}
	os.Chmod(f.config.WitnessDirectory, 0700)
	original := f.config.IdentityDirectory
	alias := filepath.Join(filepath.Dir(original), "alias")
	if os.Symlink(original, alias) != nil {
		t.Fatal("fixture symlink")
	}
	f.config.IdentityDirectory = alias
	if Initialize(f.config, f.owner) == nil {
		t.Fatal("symlink identity accepted")
	}
	f.config.IdentityDirectory = original
	shared := filepath.Join(filepath.Dir(original), "shared")
	os.Mkdir(shared, 0700)
	os.Chmod(shared, 0777)
	private := filepath.Join(shared, "private")
	os.Mkdir(private, 0700)
	if _, e := privateDirectory(private); e == nil {
		t.Fatal("nonsticky shared ancestor accepted")
	}
}
func TestRuntimeFileReplacementAndPermissions(t *testing.T) {
	f := provision(t)
	if Initialize(f.config, f.owner) != nil {
		t.Fatal("initialize")
	}
	host, e := Open(f.config)
	if e != nil {
		t.Fatal("open")
	}
	defer host.Close()
	path := filepath.Join(f.config.IdentityDirectory, "device-key.pem")
	data, _ := os.ReadFile(path)
	if os.Rename(path, path+".previous") != nil || os.WriteFile(path, data, 0600) != nil {
		t.Fatal("fixture replacement")
	}
	if host.Check() != ErrStorage {
		t.Fatal("same-byte identity replacement ignored")
	}
}
func TestStrictHostConfiguration(t *testing.T) {
	f := provision(t)
	path := filepath.Join(f.config.IdentityDirectory, "server.json")
	data, _ := json.Marshal(f.config)
	if os.WriteFile(path, data, 0600) != nil {
		t.Fatal("configuration fixture")
	}
	if _, e := LoadConfig(path); e != nil {
		t.Fatal("valid configuration")
	}
	for _, bad := range [][]byte{append(append([]byte{}, data[:len(data)-1]...), []byte(`,"extra":true}`)...), append(append([]byte{}, data[:len(data)-1]...), []byte(`,"schema":1}`)...), []byte(`null`), append(append([]byte{}, data...), []byte(`{}`)...), make([]byte, 8193)} {
		os.WriteFile(path, bad, 0600)
		if _, e := LoadConfig(path); e == nil {
			t.Fatal("ambiguous configuration accepted")
		}
	}
	os.WriteFile(path, data, 0600)
	os.Chmod(path, 0644)
	if _, e := LoadConfig(path); e == nil {
		t.Fatal("shared configuration accepted")
	}
	for _, address := range []string{"localhost:12345", "[::1]:12345", "127.0.0.1:0", "224.0.0.1:12345", "255.255.255.255:12345", f.config.BackendAddress} {
		changed := f.config
		changed.ControlAddress = address
		if changed.Validate() == nil {
			t.Fatal("invalid listener accepted")
		}
	}
	for _, directory := range []string{f.config.AuthorityDirectory, filepath.Join(f.config.AuthorityDirectory, "nested"), filepath.Dir(f.config.IdentityDirectory)} {
		changed := f.config
		changed.WitnessDirectory = directory
		if changed.Validate() == nil {
			t.Fatal("witness backup overlap accepted")
		}
	}
}
func TestWrongPrivateKeyFailsBeforeState(t *testing.T) {
	f := provision(t)
	wrong, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if e != nil {
		t.Fatal("fixture key")
	}
	key, e := x509.MarshalPKCS8PrivateKey(wrong)
	if e != nil {
		t.Fatal("fixture key encoding")
	}
	os.WriteFile(filepath.Join(f.config.IdentityDirectory, "device-key.pem"), pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: key}), 0600)
	if Initialize(f.config, f.owner) != ErrIdentity {
		t.Fatal("mismatched private key accepted")
	}
}
func TestServerRootAndOwnerBindingSurviveRestart(t *testing.T) {
	f := provision(t)
	if Initialize(f.config, f.owner) != nil {
		t.Fatal("initialize")
	}
	host, e := Open(f.config)
	if e != nil {
		t.Fatal("open")
	}
	host.Close()
	other := provision(t)
	for _, name := range []string{"device-cert.pem", "device-key.pem", "owner-root.pem"} {
		data, _ := os.ReadFile(filepath.Join(other.config.IdentityDirectory, name))
		os.WriteFile(filepath.Join(f.config.IdentityDirectory, name), data, 0600)
	}
	if changed, e := Open(f.config); e == nil {
		changed.Close()
		t.Fatal("replacement server/root adopted")
	}
}
