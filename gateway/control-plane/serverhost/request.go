package serverhost

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"os"
	"path/filepath"

	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
)

func CreateRequest(directory string) error {
	if _, e := privateDirectory(directory); e != nil {
		return e
	}
	names := []string{"device-key.pem", "device-request.pem"}
	for _, name := range names {
		if _, e := os.Lstat(filepath.Join(directory, name)); !os.IsNotExist(e) {
			return ErrStorage
		}
	}
	key, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if e != nil {
		return ErrIdentity
	}
	private, e := x509.MarshalPKCS8PrivateKey(key)
	if e != nil {
		return ErrIdentity
	}
	request, e := x509.CreateCertificateRequest(rand.Reader, &x509.CertificateRequest{Subject: pkix.Name{CommonName: authority.DeviceName}, DNSNames: []string{authority.DeviceName}, SignatureAlgorithm: x509.ECDSAWithSHA256}, key)
	if e != nil {
		return ErrIdentity
	}
	values := [][]byte{pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: private}), pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE REQUEST", Bytes: request})}
	for index, name := range names {
		file, e := privateFile(filepath.Join(directory, name), os.O_CREATE|os.O_EXCL|os.O_WRONLY)
		if e != nil {
			return e
		}
		n, e := file.Write(values[index])
		if e == nil && n != len(values[index]) {
			e = ErrStorage
		}
		if e == nil {
			e = file.Sync()
		}
		closing := file.Close()
		if e != nil || closing != nil {
			return ErrStorage
		}
	}
	return syncDirectory(directory)
}
