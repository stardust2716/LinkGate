package authority

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"math"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	jose "github.com/go-jose/go-jose/v4"
	"github.com/go-jose/go-jose/v4/jwt"
	"github.com/google/uuid"
)

const leaseType = "linkgate-lease+jwt"

var authorityLabel = regexp.MustCompile(`^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$`)
var numericAuthority = regexp.MustCompile(`^(?:(?:0[xX][0-9a-fA-F]+)|[0-9]+)(?:\.(?:(?:0[xX][0-9a-fA-F]+)|[0-9]+))*$`)
var authorityPort = regexp.MustCompile(`^[1-9][0-9]{0,4}$`)

type leaseClaims struct {
	Issuer            string      `json:"iss"`
	Subject           string      `json:"sub"`
	Audience          []string    `json:"aud"`
	ID                string      `json:"jti"`
	IssuedAt          int64       `json:"iat"`
	NotBefore         int64       `json:"nbf"`
	Expires           int64       `json:"exp"`
	Version           int         `json:"v"`
	ServerEpoch       string      `json:"serverEpoch"`
	Sequence          Revision    `json:"sequence"`
	Scope             AccessScope `json:"scope"`
	VisitorRevision   Revision    `json:"visitorRevision"`
	PublisherRevision Revision    `json:"publisherRevision"`
	ServiceRevision   Revision    `json:"serviceRevision"`
	GrantRevision     Revision    `json:"grantRevision"`
	Protocol          string      `json:"protocol"`
	Authority         string      `json:"authority"`
}

type LeaseIssuer struct {
	signer   jose.Signer
	ID       string
	Epoch    string
	sequence atomic.Uint64
}

func NewLeaseIssuer(key *ecdsa.PrivateKey) (*LeaseIssuer, error) {
	if key == nil || key.Curve != elliptic.P256() || key.D == nil || key.D.Sign() <= 0 || key.D.Cmp(elliptic.P256().Params().N) >= 0 {
		return nil, reject("IDENTITY_CHANGED")
	}
	id, err := IssuerID(&key.PublicKey)
	if err != nil {
		return nil, err
	}
	signer, err := jose.NewSigner(jose.SigningKey{Algorithm: jose.ES256, Key: key}, new(jose.SignerOptions).WithType(leaseType).WithHeader("kid", id))
	if err != nil {
		return nil, reject("IDENTITY_CHANGED")
	}
	return &LeaseIssuer{signer: signer, ID: id, Epoch: uuid.NewString()}, nil
}
func IssuerID(key *ecdsa.PublicKey) (string, error) {
	if key == nil || key.Curve != elliptic.P256() || key.X == nil || key.Y == nil || !key.Curve.IsOnCurve(key.X, key.Y) {
		return "", reject("IDENTITY_CHANGED")
	}
	encoded, err := x509.MarshalPKIXPublicKey(key)
	if err != nil {
		return "", reject("IDENTITY_CHANGED")
	}
	digest := sha256.Sum256(encoded)
	return hex.EncodeToString(digest[:]), nil
}
func validSnapshot(value Snapshot) bool {
	g, s := value.Grant, value.Service
	return g.Active && s.Active && g.Scope.Valid() && g.Scope.Target == s.Scope && g.Revision > 0 && g.VisitorRevision > 0 && g.PublisherRevision > 0 && g.ServiceRevision > 0 && s.Revision == g.ServiceRevision && s.PublisherRevision == g.PublisherRevision && validProtocol(s.Protocol, s.Authority)
}
func validProtocol(protocol, authority string) bool {
	if len(authority) > 259 || authority != strings.ToLower(authority) {
		return false
	}
	if authority != "" {
		parts := strings.Split(authority, ":")
		if len(parts) > 2 || len(parts[0]) == 0 || len(parts[0]) > 253 || numericAuthority.MatchString(parts[0]) {
			return false
		}
		for _, label := range strings.Split(parts[0], ".") {
			if !authorityLabel.MatchString(label) {
				return false
			}
		}
		if len(parts) == 2 {
			port, err := strconv.ParseUint(parts[1], 10, 16)
			if err != nil || port == 0 || !authorityPort.MatchString(parts[1]) {
				return false
			}
		}
	}
	if strings.ContainsAny(authority, "/\\@?#\x00\r\n\t ") {
		return false
	}
	switch protocol {
	case "tcp", "http", "ws":
		return true
	case "https", "wss":
		return authority != ""
	default:
		return false
	}
}
func (i *LeaseIssuer) Issue(snapshot Snapshot, now time.Time, lifetime time.Duration) (string, error) {
	if !validSnapshot(snapshot) || lifetime < time.Second || lifetime > 120*time.Second || lifetime%time.Second != 0 || now.Unix() <= 0 {
		return "", reject("AUTH_REQUIRED")
	}
	var serial uint64
	for {
		old := i.sequence.Load()
		if old == math.MaxUint64 {
			return "", reject("RESOURCE_EXHAUSTED")
		}
		if i.sequence.CompareAndSwap(old, old+1) {
			serial = old + 1
			break
		}
	}
	g, s := snapshot.Grant, snapshot.Service
	claims := leaseClaims{Issuer: i.ID, Subject: g.Scope.VisitorDevice, Audience: []string{s.Scope.PublisherDevice}, ID: uuid.NewString(), IssuedAt: now.Unix(), NotBefore: now.Unix(), Expires: now.Unix() + int64(lifetime/time.Second), Version: 1, ServerEpoch: i.Epoch, Sequence: Revision(serial), Scope: g.Scope, VisitorRevision: g.VisitorRevision, PublisherRevision: g.PublisherRevision, ServiceRevision: g.ServiceRevision, GrantRevision: g.Revision, Protocol: s.Protocol, Authority: s.Authority}
	payload, err := encode(claims)
	if err != nil {
		return "", err
	}
	signed, err := i.signer.Sign(payload)
	if err != nil {
		return "", reject("STORAGE_UNAVAILABLE")
	}
	token, err := signed.CompactSerialize()
	if err != nil || len(token) > 16384 {
		return "", reject("STORAGE_UNAVAILABLE")
	}
	return token, nil
}

type LeaseReceipt struct {
	ID            string
	Issuer        string
	ServerEpoch   string
	Sequence      Revision
	Scope         AccessScope
	AcceptedNanos int64
	DeadlineNanos int64
	VerifiedUTC   time.Time
	DeadlineUTC   time.Time
}

func (r LeaseReceipt) Active(nowNanos int64) bool {
	return nowNanos >= r.AcceptedNanos && nowNanos < r.DeadlineNanos
}

// VerifyLease needs a current authenticated authority snapshot and current server boot epoch.
// Token-supplied keys, issuer identities and boot epochs are never trust inputs.
func VerifyLease(token string, key *ecdsa.PublicKey, issuer, epoch string, current Snapshot, now time.Time, monoNanos int64) (LeaseReceipt, error) {
	fail := func() (LeaseReceipt, error) { return LeaseReceipt{}, reject("AUTH_REQUIRED") }
	if len(token) == 0 || len(token) > 16384 || !validSnapshot(current) || !validKey(issuer) || !validUUID(epoch) || monoNanos < 0 {
		return fail()
	}
	keyID, err := IssuerID(key)
	if err != nil || keyID != issuer {
		return fail()
	}
	parts := strings.Split(token, ".")
	if len(parts) != 3 || len(parts[0]) > 2048 || len(parts[2]) != 86 {
		return fail()
	}
	for _, part := range parts {
		if _, err := base64.RawURLEncoding.Strict().DecodeString(part); err != nil {
			return fail()
		}
	}
	headerBytes, err := base64.RawURLEncoding.Strict().DecodeString(parts[0])
	if err != nil {
		return fail()
	}
	var header struct {
		Algorithm string `json:"alg"`
		Type      string `json:"typ"`
		KeyID     string `json:"kid"`
	}
	if decode(headerBytes, &header) != nil || header.Algorithm != "ES256" || header.Type != leaseType || header.KeyID != issuer {
		return fail()
	}
	signed, err := jose.ParseSigned(token, []jose.SignatureAlgorithm{jose.ES256})
	if err != nil {
		return fail()
	}
	payload, err := signed.Verify(key)
	if err != nil {
		return fail()
	}
	var claims leaseClaims
	if decode(payload, &claims) != nil || claims.Version != 1 || !validUUID(claims.ID) || claims.Sequence == 0 || claims.ServerEpoch != epoch || claims.Scope != current.Grant.Scope || claims.VisitorRevision != current.Grant.VisitorRevision || claims.PublisherRevision != current.Grant.PublisherRevision || claims.ServiceRevision != current.Service.Revision || claims.GrantRevision != current.Grant.Revision || claims.Protocol != current.Service.Protocol || claims.Authority != current.Service.Authority {
		return fail()
	}
	if claims.IssuedAt <= 0 || claims.IssuedAt > now.Unix() || claims.NotBefore != claims.IssuedAt || claims.Expires <= claims.IssuedAt || claims.Expires-claims.IssuedAt > 120 || claims.Expires <= now.Unix() || len(claims.Audience) != 1 {
		return fail()
	}
	standard := jwt.Claims{Issuer: claims.Issuer, Subject: claims.Subject, Audience: jwt.Audience(claims.Audience), ID: claims.ID, IssuedAt: jwt.NewNumericDate(time.Unix(claims.IssuedAt, 0)), NotBefore: jwt.NewNumericDate(time.Unix(claims.NotBefore, 0)), Expiry: jwt.NewNumericDate(time.Unix(claims.Expires, 0))}
	if standard.ValidateWithLeeway(jwt.Expected{Issuer: issuer, Subject: current.Grant.Scope.VisitorDevice, AnyAudience: jwt.Audience{current.Service.Scope.PublisherDevice}, Time: now}, 0) != nil {
		return fail()
	}
	remaining := time.Unix(claims.Expires, 0).Sub(now)
	if remaining <= 0 || remaining > 120*time.Second || monoNanos > math.MaxInt64-int64(remaining) {
		return fail()
	}
	return LeaseReceipt{ID: claims.ID, Issuer: issuer, ServerEpoch: epoch, Sequence: claims.Sequence, Scope: claims.Scope, AcceptedNanos: monoNanos, DeadlineNanos: monoNanos + int64(remaining), VerifiedUTC: now.UTC(), DeadlineUTC: time.Unix(claims.Expires, 0)}, nil
}

// One LeaseWindow belongs to one exact scope on one authenticated control generation.
type LeaseWindow struct {
	mu           sync.Mutex
	receipt      LeaseReceipt
	set          bool
	lastNanos    int64
	closed       bool
	effectiveUTC time.Time
}

func (w *LeaseWindow) observe(nowNanos int64) error {
	if w.closed {
		return reject("AUTH_REQUIRED")
	}
	if nowNanos < w.lastNanos {
		w.closed = true
		return reject("AUTH_REQUIRED")
	}
	if !w.effectiveUTC.IsZero() {
		w.effectiveUTC = w.effectiveUTC.Add(time.Duration(nowNanos - w.lastNanos))
	}
	w.lastNanos = nowNanos
	return nil
}

func (w *LeaseWindow) Accept(receipt LeaseReceipt, nowNanos int64) (LeaseReceipt, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	if err := w.observe(nowNanos); err != nil {
		return LeaseReceipt{}, err
	}
	if !receipt.Active(nowNanos) {
		return LeaseReceipt{}, reject("AUTH_REQUIRED")
	}
	if receipt.VerifiedUTC.IsZero() || receipt.DeadlineUTC.IsZero() {
		return LeaseReceipt{}, reject("AUTH_REQUIRED")
	}
	if w.set && (receipt.Scope != w.receipt.Scope || receipt.Issuer != w.receipt.Issuer || receipt.ServerEpoch != w.receipt.ServerEpoch) {
		return LeaseReceipt{}, reject("AUTH_REQUIRED")
	}
	observedUTC := receipt.VerifiedUTC.Add(time.Duration(nowNanos - receipt.AcceptedNanos))
	if w.effectiveUTC.IsZero() || observedUTC.After(w.effectiveUTC) {
		w.effectiveUTC = observedUTC
	}
	remainingUTC := receipt.DeadlineUTC.Sub(w.effectiveUTC)
	if remainingUTC <= 0 || remainingUTC > 120*time.Second || nowNanos > math.MaxInt64-int64(remainingUTC) {
		return LeaseReceipt{}, reject("AUTH_REQUIRED")
	}
	if bound := nowNanos + int64(remainingUTC); bound < receipt.DeadlineNanos {
		receipt.DeadlineNanos = bound
	}
	if w.set {
		if receipt.Scope != w.receipt.Scope || receipt.Issuer != w.receipt.Issuer || receipt.ServerEpoch != w.receipt.ServerEpoch {
			return LeaseReceipt{}, reject("AUTH_REQUIRED")
		}
		if receipt.ID == w.receipt.ID {
			if receipt.DeadlineNanos < w.receipt.DeadlineNanos {
				w.receipt.DeadlineNanos = receipt.DeadlineNanos
			}
			if receipt.Sequence != w.receipt.Sequence || !w.receipt.Active(nowNanos) {
				return LeaseReceipt{}, reject("AUTH_REQUIRED")
			}
			w.lastNanos = nowNanos
			return w.receipt, nil
		}
		if receipt.Sequence <= w.receipt.Sequence {
			return LeaseReceipt{}, reject("AUTH_REQUIRED")
		}
	}
	w.receipt = receipt
	w.set = true
	w.lastNanos = nowNanos
	return receipt, nil
}
func (w *LeaseWindow) Close() { w.mu.Lock(); defer w.mu.Unlock(); w.closed = true }
func (w *LeaseWindow) Current(nowNanos int64) (LeaseReceipt, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	if err := w.observe(nowNanos); err != nil {
		return LeaseReceipt{}, err
	}
	if w.closed || !w.set || !w.receipt.Active(nowNanos) || !w.effectiveUTC.Before(w.receipt.DeadlineUTC) {
		return LeaseReceipt{}, reject("AUTH_REQUIRED")
	}
	w.lastNanos = nowNanos
	return w.receipt, nil
}
