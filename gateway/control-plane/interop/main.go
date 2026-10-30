package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json/v2"
	"encoding/pem"
	"fmt"
	"math/big"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	jose "github.com/go-jose/go-jose/v4"
	a "github.com/stardust2716/Redesk/gateway/control-plane/authority"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "Server-authority fixture creation failed.")
		os.Exit(1)
	}
	fmt.Println(`{"created":true,"privateKeyExported":false}`)
}
func run() error {
	if len(os.Args) != 2 {
		return fmt.Errorf("fixture directory required")
	}
	directory, err := filepath.Abs(os.Args[1])
	if err != nil {
		return err
	}
	info, err := os.Stat(directory)
	if err != nil || !info.IsDir() {
		return fmt.Errorf("protected fixture directory required")
	}
	rootKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return err
	}
	deviceKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return err
	}
	now := time.Now().Truncate(time.Second)
	rootTemplate := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "LinkGate interoperability root"}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(72 * time.Hour), IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign | x509.KeyUsageCRLSign}
	rootDER, err := x509.CreateCertificate(rand.Reader, rootTemplate, rootTemplate, &rootKey.PublicKey, rootKey)
	if err != nil {
		return err
	}
	root, err := x509.ParseCertificate(rootDER)
	if err != nil {
		return err
	}
	leafTemplate := &x509.Certificate{SerialNumber: big.NewInt(2), Subject: pkix.Name{CommonName: a.DeviceName}, DNSNames: []string{a.DeviceName}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(24 * time.Hour), BasicConstraintsValid: true, KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth, x509.ExtKeyUsageServerAuth}}
	leafDER, err := x509.CreateCertificate(rand.Reader, leafTemplate, root, &deviceKey.PublicKey, rootKey)
	if err != nil {
		return err
	}
	identity, err := a.NewDeviceIdentity(tls.Certificate{Certificate: [][]byte{leafDER, rootDER}, PrivateKey: deviceKey}, root, now)
	if err != nil {
		return err
	}
	issuer, err := a.NewLeaseIssuer(deviceKey)
	if err != nil {
		return err
	}
	scope := a.AccessScope{VisitorDevice: strings.Repeat("a", 64), VisitorApp: "org.example.client", Target: a.ServiceScope{PublisherDevice: strings.Repeat("b", 64), PublisherApp: "org.example.publisher", Service: "files"}}
	snapshot := a.Snapshot{Grant: a.Grant{Scope: scope, Revision: 5, VisitorRevision: 4, PublisherRevision: 3, ServiceRevision: 2, Active: true}, Service: a.Service{Scope: scope.Target, Revision: 2, PublisherRevision: 3, Active: true, Protocol: "https", Authority: "files.example.invalid"}}
	first, err := issuer.Issue(snapshot, now, 120*time.Second)
	if err != nil {
		return err
	}
	second, err := issuer.Issue(snapshot, now, 120*time.Second)
	if err != nil {
		return err
	}
	signed, err := jose.ParseSigned(first, []jose.SignatureAlgorithm{jose.ES256})
	if err != nil {
		return err
	}
	payload, err := signed.Verify(&deviceKey.PublicKey)
	if err != nil {
		return err
	}
	signer, err := jose.NewSigner(jose.SigningKey{Algorithm: jose.ES256, Key: deviceKey}, new(jose.SignerOptions).WithType("linkgate-lease+jwt").WithHeader("kid", identity.ID))
	if err != nil {
		return err
	}
	mutations := map[string]string{
		"duplicate": strings.Replace(string(payload), `"v":1`, `"v":1,"v":1`, 1),
		"unknown":   strings.Replace(string(payload), `"v":1`, `"v":1,"unrecognized":true`, 1),
		"counter":   strings.Replace(string(payload), `"sequence":"1"`, `"sequence":"01"`, 1),
		"exponent":  strings.Replace(string(payload), `"iat":`+strconv.FormatInt(now.Unix(), 10), `"iat":`+strconv.FormatInt(now.Unix()/10, 10)+`e1`, 1),
		"lifetime":  strings.Replace(string(payload), `"exp":`+strconv.FormatInt(now.Unix()+120, 10), `"exp":`+strconv.FormatInt(now.Unix()+121, 10), 1),
		"scope":     strings.Replace(string(payload), `"org.example.client"`, `"org.example.other"`, 1),
		"unicode":   strings.Replace(string(payload), `"org.example.client"`, `"org.example.\ud800"`, 1),
	}
	invalid := make(map[string]string)
	for name, value := range mutations {
		if value == string(payload) {
			return fmt.Errorf("fixture mutation unchanged")
		}
		message, e := signer.Sign([]byte(value))
		if e != nil {
			return e
		}
		invalid[name], e = message.CompactSerialize()
		if e != nil {
			return e
		}
	}
	write := func(name string, data []byte) error {
		file, err := os.OpenFile(filepath.Join(directory, name), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
		if err != nil {
			return err
		}
		defer file.Close()
		if _, err = file.Write(data); err != nil {
			return err
		}
		return file.Sync()
	}
	badTemplate := *leafTemplate
	badTemplate.DNSNames = nil
	badDER, err := x509.CreateCertificate(rand.Reader, &badTemplate, root, &deviceKey.PublicKey, rootKey)
	if err != nil {
		return err
	}
	for name, certificate := range map[string][]byte{"server-cert.pem": leafDER, "owner-root.pem": rootDER, "missing-san-cert.pem": badDER} {
		if err = write(name, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: certificate})); err != nil {
			return err
		}
	}
	metadata := struct {
		Issuer string `json:"issuer"`
		Epoch  string `json:"epoch"`
		Issued int64  `json:"issuedUnix"`
	}{issuer.ID, issuer.Epoch, now.Unix()}
	for name, value := range map[string]any{"snapshot.json": snapshot, "metadata.json": metadata, "invalid.json": invalid} {
		data, e := json.Marshal(value)
		if e != nil {
			return e
		}
		if err = write(name, data); err != nil {
			return err
		}
	}
	if err = write("first.jwt", []byte(first)); err != nil {
		return err
	}
	return write("next.jwt", []byte(second))
}
