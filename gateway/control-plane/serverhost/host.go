package serverhost

import (
	"crypto/ecdsa"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json/v2"
	"encoding/pem"
	"errors"
	"io"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
	bolt "go.etcd.io/bbolt"
)

var ErrConfiguration = errors.New("INVALID_REQUEST")
var ErrStorage = errors.New("STORAGE_UNAVAILABLE")
var ErrIdentity = errors.New("IDENTITY_CHANGED")
var ErrPlatform = errors.New("HOST_PLATFORM_UNSUPPORTED")
var ErrClock = errors.New("CLOCK_UNTRUSTED")

type Config struct {
	Schema             int    `json:"schema"`
	IdentityDirectory  string `json:"identityDirectory"`
	AuthorityDirectory string `json:"authorityDirectory"`
	WitnessDirectory   string `json:"witnessDirectory"`
	ControlAddress     string `json:"controlAddress"`
	BackendAddress     string `json:"backendAddress"`
}

func (c Config) Validate() error {
	if c.Schema != 1 {
		return ErrConfiguration
	}
	for _, p := range []string{c.IdentityDirectory, c.AuthorityDirectory, c.WitnessDirectory} {
		if !filepath.IsAbs(p) || filepath.Clean(p) != p {
			return ErrConfiguration
		}
	}
	contains := func(a, b string) bool {
		relative, e := filepath.Rel(a, b)
		return e == nil && (relative == "." || relative != ".." && !filepath.IsAbs(relative) && !strings.HasPrefix(relative, ".."+string(filepath.Separator)))
	}
	for _, p := range []string{c.AuthorityDirectory, c.IdentityDirectory} {
		if contains(p, c.WitnessDirectory) || contains(c.WitnessDirectory, p) {
			return ErrConfiguration
		}
	}
	var ports []uint16
	for _, value := range []string{c.ControlAddress, c.BackendAddress} {
		endpoint, e := netip.ParseAddrPort(value)
		if e != nil || endpoint.String() != value || !endpoint.Addr().Is4() || endpoint.Addr().IsMulticast() || endpoint.Addr() == netip.MustParseAddr("255.255.255.255") || endpoint.Port() == 0 {
			return ErrConfiguration
		}
		ports = append(ports, endpoint.Port())
	}
	if ports[0] == ports[1] {
		return ErrConfiguration
	}
	return nil
}
func LoadConfig(path string) (Config, error) {
	var config Config
	data, e := readPrivate(path, 8192)
	if e != nil {
		return config, e
	}
	if json.Unmarshal(data, &config, json.RejectUnknownMembers(true)) != nil || config.Validate() != nil {
		return Config{}, ErrConfiguration
	}
	return config, nil
}
func ReadOwnerCertificate(path string) ([]byte, error) {
	data, e := readPrivate(path, 16384)
	if e != nil {
		return nil, e
	}
	if _, e = certificate(data); e != nil {
		return nil, e
	}
	return data, nil
}
func readPrivate(path string, limit int) ([]byte, error) {
	file, e := privateFile(path, os.O_RDONLY)
	if e != nil {
		return nil, e
	}
	defer file.Close()
	data, e := io.ReadAll(io.LimitReader(file, int64(limit+1)))
	if e != nil || len(data) > limit || len(data) == 0 {
		return nil, ErrStorage
	}
	return data, nil
}
func certificate(data []byte) (*x509.Certificate, error) {
	block, rest := pem.Decode(data)
	if block == nil || block.Type != "CERTIFICATE" || len(rest) != 0 {
		return nil, ErrIdentity
	}
	cert, e := x509.ParseCertificate(block.Bytes)
	if e != nil {
		return nil, ErrIdentity
	}
	return cert, nil
}
func loadIdentity(config Config) (*authority.DeviceIdentity, error) {
	var values [][]byte
	for _, name := range []string{"device-cert.pem", "device-key.pem", "owner-root.pem"} {
		value, e := readPrivate(filepath.Join(config.IdentityDirectory, name), 16384)
		if e != nil {
			return nil, e
		}
		values = append(values, value)
	}
	root, e := certificate(values[2])
	if e != nil {
		return nil, e
	}
	pair, e := tls.X509KeyPair(values[0], values[1])
	if e != nil {
		return nil, ErrIdentity
	}
	identity, e := authority.NewDeviceIdentity(pair, root, time.Now())
	if e != nil {
		return nil, ErrIdentity
	}
	return identity, nil
}

type protectedPath struct {
	path      string
	file      os.FileInfo
	directory bool
}
type Host struct {
	Identity          *authority.DeviceIdentity
	Store             *authority.Store
	database, witness *bolt.DB
	paths             []protectedPath
	once              sync.Once
}

func Initialize(config Config, ownerPEM []byte) error {
	host, e := open(config, true, ownerPEM)
	if e != nil {
		return e
	}
	return host.Close()
}
func Open(config Config) (*Host, error) { return open(config, false, nil) }
func open(config Config, create bool, ownerPEM []byte) (*Host, error) {
	if e := config.Validate(); e != nil {
		return nil, e
	}
	host := &Host{}
	success := false
	defer func() {
		if !success {
			_ = host.Close()
		}
	}()
	for _, path := range []string{config.IdentityDirectory, config.AuthorityDirectory, config.WitnessDirectory} {
		info, e := privateDirectory(path)
		if e != nil {
			return nil, e
		}
		host.paths = append(host.paths, protectedPath{path, info, true})
	}
	identity, e := loadIdentity(config)
	if e != nil {
		return nil, e
	}
	host.Identity = identity
	ownerID := ""
	if create {
		owner, e := certificate(ownerPEM)
		if e != nil || owner.IsCA {
			return nil, ErrIdentity
		}
		roots := x509.NewCertPool()
		roots.AddCert(identity.Root)
		if _, e = owner.Verify(x509.VerifyOptions{Roots: roots, CurrentTime: time.Now(), DNSName: authority.DeviceName, KeyUsages: []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth, x509.ExtKeyUsageServerAuth}}); e != nil {
			return nil, ErrIdentity
		}
		client, server := false, false
		for _, usage := range owner.ExtKeyUsage {
			client = client || usage == x509.ExtKeyUsageClientAuth
			server = server || usage == x509.ExtKeyUsageServerAuth
		}
		key, ok := owner.PublicKey.(*ecdsa.PublicKey)
		if !ok || !client || !server {
			return nil, ErrIdentity
		}
		ownerID, e = authority.IssuerID(key)
		if e != nil {
			return nil, ErrIdentity
		}
	}
	for _, name := range []string{"device-cert.pem", "device-key.pem", "owner-root.pem"} {
		path := filepath.Join(config.IdentityDirectory, name)
		file, e := privateFile(path, os.O_RDONLY)
		if e != nil {
			return nil, e
		}
		info, e := file.Stat()
		file.Close()
		if e != nil {
			return nil, ErrStorage
		}
		host.paths = append(host.paths, protectedPath{path, info, false})
	}
	paths := []string{filepath.Join(config.AuthorityDirectory, "authority.db"), filepath.Join(config.WitnessDirectory, "witness.db")}
	// Preflight all existing paths before creating either side. Failed initialization
	// is intentionally not repaired or overwritten on a later ordinary startup.
	for _, path := range paths {
		_, e := os.Lstat(path)
		if create && !os.IsNotExist(e) {
			return nil, ErrStorage
		}
		if !create && e != nil {
			return nil, ErrStorage
		}
	}
	var databases []*bolt.DB
	for _, path := range paths {
		flags := os.O_RDWR
		if create {
			flags |= os.O_CREATE | os.O_EXCL
		}
		file, e := privateFile(path, flags)
		if e != nil {
			return nil, e
		}
		info, e := file.Stat()
		if e == nil && create {
			e = file.Sync()
		}
		file.Close()
		if e != nil {
			return nil, ErrStorage
		}
		if create && syncDirectory(filepath.Dir(path)) != nil {
			return nil, ErrStorage
		}
		database, e := bolt.Open(path, 0600, &bolt.Options{Timeout: 100 * time.Millisecond, OpenFile: func(name string, flags int, _ os.FileMode) (*os.File, error) {
			if name != path {
				return nil, ErrStorage
			}
			return privateFile(name, flags & ^(os.O_CREATE|os.O_EXCL))
		}})
		if e != nil {
			return nil, ErrStorage
		}
		if len(databases) == 0 {
			host.database = database
		} else {
			host.witness = database
		}
		databases = append(databases, database)
		current, e := os.Lstat(path)
		if e != nil || !os.SameFile(info, current) {
			return nil, ErrStorage
		}
		host.paths = append(host.paths, protectedPath{path, info, false})
	}
	witness, e := authority.NewBoltWitness(host.witness, create)
	if e != nil {
		return nil, ErrStorage
	}
	root := sha256.Sum256(identity.Root.Raw)
	host.Store, e = authority.NewStore(host.database, witness, identity.ID, hex.EncodeToString(root[:]), ownerID, time.Now(), create)
	if e != nil {
		if authority.Code(e) == "CLOCK_UNTRUSTED" {
			return nil, ErrClock
		}
		return nil, ErrStorage
	}
	if e = host.Check(); e != nil {
		return nil, e
	}
	success = true
	return host, nil
}
func (h *Host) Check() error {
	for _, saved := range h.paths {
		var info os.FileInfo
		var e error
		if saved.directory {
			info, e = privateDirectory(saved.path)
		} else {
			var file *os.File
			file, e = privateFile(saved.path, os.O_RDONLY)
			if e == nil {
				info, e = file.Stat()
				file.Close()
			}
		}
		if e != nil || !os.SameFile(saved.file, info) {
			return ErrStorage
		}
	}
	now := time.Now()
	if now.Before(h.Identity.Leaf.NotBefore) || !now.Before(h.Identity.Leaf.NotAfter) || now.Before(h.Identity.Root.NotBefore) || !now.Before(h.Identity.Root.NotAfter) {
		return ErrIdentity
	}
	return nil
}
func (h *Host) Close() error {
	var failed bool
	h.once.Do(func() {
		if h.database != nil {
			failed = h.database.Close() != nil
		}
		if h.witness != nil {
			failed = h.witness.Close() != nil || failed
		}
	})
	if failed {
		return ErrStorage
	}
	return nil
}
