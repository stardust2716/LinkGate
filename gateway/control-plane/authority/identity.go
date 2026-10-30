package authority

import (
	"crypto"
	"crypto/ecdsa"
	"crypto/tls"
	"crypto/x509"
	"time"
)

const DeviceName = "linkgate.device"

type DeviceIdentity struct {
	Certificate tls.Certificate
	Root        *x509.Certificate
	Leaf        *x509.Certificate
	ID          string
}

func NewDeviceIdentity(certificate tls.Certificate, root *x509.Certificate, now time.Time) (*DeviceIdentity, error) {
	if len(certificate.Certificate) < 1 || root == nil || !root.IsCA {
		return nil, reject("IDENTITY_CHANGED")
	}
	leaf, err := x509.ParseCertificate(certificate.Certificate[0])
	if err != nil || validateDeviceProfile(leaf, root, now) != nil {
		return nil, reject("IDENTITY_CHANGED")
	}
	key, ok := certificate.PrivateKey.(crypto.Signer)
	if !ok {
		return nil, reject("IDENTITY_CHANGED")
	}
	public, ok := key.Public().(*ecdsa.PublicKey)
	if !ok {
		return nil, reject("IDENTITY_CHANGED")
	}
	id, err := IssuerID(public)
	if err != nil {
		return nil, err
	}
	leafKey, ok := leaf.PublicKey.(*ecdsa.PublicKey)
	if !ok {
		return nil, reject("IDENTITY_CHANGED")
	}
	leafID, err := IssuerID(leafKey)
	if err != nil || leafID != id {
		return nil, reject("IDENTITY_CHANGED")
	}
	certificate.Leaf = leaf
	return &DeviceIdentity{Certificate: certificate, Root: root, Leaf: leaf, ID: id}, nil
}
func validateDeviceProfile(leaf, root *x509.Certificate, now time.Time) error {
	if leaf == nil || root == nil || !root.IsCA || leaf.IsCA || now.Before(root.NotBefore) || !now.Before(root.NotAfter) || now.Before(leaf.NotBefore) || !now.Before(leaf.NotAfter) {
		return reject("IDENTITY_CHANGED")
	}
	key, ok := leaf.PublicKey.(*ecdsa.PublicKey)
	if !ok {
		return reject("IDENTITY_CHANGED")
	}
	if _, err := IssuerID(key); err != nil {
		return err
	}
	client, server := false, false
	for _, usage := range leaf.ExtKeyUsage {
		if usage == x509.ExtKeyUsageClientAuth {
			client = true
		}
		if usage == x509.ExtKeyUsageServerAuth {
			server = true
		}
	}
	exactName := false
	for _, name := range leaf.DNSNames {
		if name == DeviceName {
			exactName = true
		}
	}
	if !client || !server || !exactName || leaf.VerifyHostname(DeviceName) != nil {
		return reject("IDENTITY_CHANGED")
	}
	pool := x509.NewCertPool()
	pool.AddCert(root)
	if _, err := leaf.Verify(x509.VerifyOptions{Roots: pool, CurrentTime: now, DNSName: DeviceName, KeyUsages: []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth, x509.ExtKeyUsageServerAuth}}); err != nil {
		return reject("IDENTITY_CHANGED")
	}
	return nil
}
func (i *DeviceIdentity) ServerTLS(admit func(string) bool, clock func() time.Time) (*tls.Config, error) {
	if admit == nil || clock == nil || validateDeviceProfile(i.Leaf, i.Root, clock()) != nil {
		return nil, reject("IDENTITY_CHANGED")
	}
	pool := x509.NewCertPool()
	pool.AddCert(i.Root)
	return &tls.Config{Certificates: []tls.Certificate{i.Certificate}, ClientAuth: tls.RequireAndVerifyClientCert, ClientCAs: pool, MinVersion: tls.VersionTLS12, SessionTicketsDisabled: true, Time: clock, VerifyConnection: func(state tls.ConnectionState) error {
		if len(state.VerifiedChains) == 0 || len(state.PeerCertificates) == 0 || validateDeviceProfile(i.Leaf, i.Root, clock()) != nil || validateDeviceProfile(state.PeerCertificates[0], i.Root, clock()) != nil {
			return reject("AUTH_REQUIRED")
		}
		key, ok := state.PeerCertificates[0].PublicKey.(*ecdsa.PublicKey)
		if !ok {
			return reject("AUTH_REQUIRED")
		}
		id, err := IssuerID(key)
		if err != nil || !admit(id) {
			return reject("AUTH_REQUIRED")
		}
		return nil
	}}, nil
}
func (i *DeviceIdentity) ClientTLS(expectedServer string, clock func() time.Time) (*tls.Config, error) {
	if !validKey(expectedServer) || clock == nil || validateDeviceProfile(i.Leaf, i.Root, clock()) != nil {
		return nil, reject("IDENTITY_CHANGED")
	}
	pool := x509.NewCertPool()
	pool.AddCert(i.Root)
	return &tls.Config{Certificates: []tls.Certificate{i.Certificate}, RootCAs: pool, ServerName: DeviceName, MinVersion: tls.VersionTLS12, Time: clock, VerifyConnection: func(state tls.ConnectionState) error {
		if len(state.VerifiedChains) == 0 || len(state.PeerCertificates) == 0 || validateDeviceProfile(i.Leaf, i.Root, clock()) != nil || validateDeviceProfile(state.PeerCertificates[0], i.Root, clock()) != nil {
			return reject("IDENTITY_CHANGED")
		}
		key, ok := state.PeerCertificates[0].PublicKey.(*ecdsa.PublicKey)
		if !ok {
			return reject("IDENTITY_CHANGED")
		}
		id, err := IssuerID(key)
		if err != nil || id != expectedServer {
			return reject("IDENTITY_CHANGED")
		}
		return nil
	}}, nil
}
func HandshakeDevice(state tls.ConnectionState) (string, error) {
	if !state.HandshakeComplete || len(state.VerifiedChains) == 0 || len(state.PeerCertificates) == 0 {
		return "", reject("AUTH_REQUIRED")
	}
	key, ok := state.PeerCertificates[0].PublicKey.(*ecdsa.PublicKey)
	if !ok {
		return "", reject("AUTH_REQUIRED")
	}
	return IssuerID(key)
}
