package serverhost

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/x509"
	"encoding/pem"
	"os"
	"path/filepath"
	"runtime"
	"testing"

	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
)

func TestLocalRequestProofAndNoRotation(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("protected request creation requires Linux")
	}
	directory := t.TempDir()
	if os.Chmod(directory, 0700) != nil {
		t.Fatal("private fixture directory")
	}
	if e := CreateRequest(directory); e != nil {
		t.Fatalf("request generation: %s", e.Error())
	}
	read := func(name string) []byte {
		file, e := privateFile(filepath.Join(directory, name), os.O_RDONLY)
		if e != nil {
			t.Fatal("request output privacy")
		}
		file.Close()
		data, e := os.ReadFile(filepath.Join(directory, name))
		if e != nil {
			t.Fatal("fixture read")
		}
		return data
	}
	privateBytes, requestBytes := read("device-key.pem"), read("device-request.pem")
	privateBlock, _ := pem.Decode(privateBytes)
	requestBlock, _ := pem.Decode(requestBytes)
	key, e := x509.ParsePKCS8PrivateKey(privateBlock.Bytes)
	if e != nil {
		t.Fatal("standard PKCS8 private key")
	}
	request, e := x509.ParseCertificateRequest(requestBlock.Bytes)
	if e != nil || request.CheckSignature() != nil || len(request.DNSNames) != 1 || request.DNSNames[0] != authority.DeviceName {
		t.Fatal("standard device PKCS10 proof")
	}
	expected, e := authority.IssuerID(&key.(*ecdsa.PrivateKey).PublicKey)
	if e != nil {
		t.Fatal("private profile")
	}
	actual, e := authority.IssuerID(request.PublicKey.(*ecdsa.PublicKey))
	if e != nil || actual != expected {
		t.Fatal("CSR key mismatch")
	}
	if CreateRequest(directory) != ErrStorage || !bytes.Equal(privateBytes, read("device-key.pem")) || !bytes.Equal(requestBytes, read("device-request.pem")) {
		t.Fatal("repeated request replaced identity")
	}
}
func TestRequestPartialStateRemainsExplicit(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("protected request creation requires Linux")
	}
	directory := t.TempDir()
	if os.Chmod(directory, 0700) != nil {
		t.Fatal("private fixture directory")
	}
	path := filepath.Join(directory, "device-key.pem")
	original := []byte("owned partial key")
	if os.WriteFile(path, original, 0600) != nil {
		t.Fatal("fixture write")
	}
	if CreateRequest(directory) != ErrStorage {
		t.Fatal("partial identity replaced")
	}
	current, _ := os.ReadFile(path)
	if !bytes.Equal(current, original) {
		t.Fatal("partial key changed")
	}
	if _, e := os.Lstat(filepath.Join(directory, "device-request.pem")); !os.IsNotExist(e) {
		t.Fatal("request created from invalid partial state")
	}
}
