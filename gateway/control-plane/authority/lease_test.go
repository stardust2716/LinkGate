package authority

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"encoding/base64"
	"math"
	"strings"
	"testing"
	"time"

	jose "github.com/go-jose/go-jose/v4"
)

func leaseFixture(t *testing.T) (*LeaseIssuer, *ecdsa.PrivateKey, Snapshot, time.Time) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	issuer, err := NewLeaseIssuer(key)
	if err != nil {
		t.Fatal(err)
	}
	scope := AccessScope{VisitorDevice: strings.Repeat("a", 64), VisitorApp: "org.example.client", Target: ServiceScope{PublisherDevice: strings.Repeat("b", 64), PublisherApp: "org.example.publisher", Service: "files"}}
	snapshot := Snapshot{Grant: Grant{Scope: scope, Revision: 5, VisitorRevision: 4, PublisherRevision: 3, ServiceRevision: 2, Active: true}, Service: Service{Scope: scope.Target, Revision: 2, PublisherRevision: 3, Active: true, Protocol: "https", Authority: "files.example.invalid"}}
	return issuer, key, snapshot, time.Unix(1793260800, 0)
}
func tokenFixture(t *testing.T, issuer *LeaseIssuer, snapshot Snapshot, now time.Time) string {
	t.Helper()
	token, err := issuer.Issue(snapshot, now, 120*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	return token
}
func TestLeaseExactScopeAndMonotonicDeadline(t *testing.T) {
	issuer, key, snapshot, now := leaseFixture(t)
	token := tokenFixture(t, issuer, snapshot, now)
	receipt, err := VerifyLease(token, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now.Add(250*time.Millisecond), 1000)
	if err != nil || receipt.Scope != snapshot.Grant.Scope || receipt.DeadlineNanos != 1000+int64(119750*time.Millisecond) || !receipt.Active(receipt.DeadlineNanos-1) || receipt.Active(receipt.DeadlineNanos) {
		t.Fatalf("invalid monotonic receipt: %v", err)
	}
	changed := snapshot
	changed.Grant.Revision++
	for _, candidate := range []Snapshot{changed, {Grant: snapshot.Grant, Service: Service{Scope: snapshot.Service.Scope, Revision: 99, Active: true}}} {
		if _, err := VerifyLease(token, &key.PublicKey, issuer.ID, issuer.Epoch, candidate, now, 1000); Code(err) != "AUTH_REQUIRED" {
			t.Fatal("changed authority accepted")
		}
	}
	changed = snapshot
	changed.Grant.Scope.VisitorApp = "org.example.other"
	if _, err := VerifyLease(token, &key.PublicKey, issuer.ID, issuer.Epoch, changed, now, 1000); err == nil {
		t.Fatal("cross application accepted")
	}
	if _, err := VerifyLease(token, &key.PublicKey, issuer.ID, "00000000-0000-4000-8000-000000000000", snapshot, now, 1000); err == nil {
		t.Fatal("wrong server boot accepted")
	}
}
func TestLeaseTimeBoundsAndIssuerKeys(t *testing.T) {
	issuer, key, snapshot, now := leaseFixture(t)
	token := tokenFixture(t, issuer, snapshot, now)
	for _, when := range []time.Time{now.Add(-time.Second), now.Add(120 * time.Second), now.Add(121 * time.Second)} {
		if _, err := VerifyLease(token, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, when, 1); err == nil {
			t.Fatal("invalid wall time accepted")
		}
	}
	if _, err := VerifyLease(token, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now, math.MaxInt64-1); err == nil {
		t.Fatal("deadline overflow accepted")
	}
	for _, duration := range []time.Duration{0, 500 * time.Millisecond, 121 * time.Second, 120*time.Second + 1} {
		if _, err := issuer.Issue(snapshot, now, duration); err == nil {
			t.Fatal("invalid lifetime issued")
		}
	}
	other, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if _, err := VerifyLease(token, &other.PublicKey, issuer.ID, issuer.Epoch, snapshot, now, 1); err == nil {
		t.Fatal("wrong issuer key accepted")
	}
	wrongCurve, _ := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	if _, err := NewLeaseIssuer(wrongCurve); err == nil {
		t.Fatal("non-P256 issuer accepted")
	}
	issuer.sequence.Store(math.MaxUint64)
	if _, err := issuer.Issue(snapshot, now, time.Second); Code(err) != "RESOURCE_EXHAUSTED" {
		t.Fatal("sequence overflow accepted")
	}
}
func TestSignedMalformedClaimsAndHeadersAreRefused(t *testing.T) {
	issuer, key, snapshot, now := leaseFixture(t)
	token := tokenFixture(t, issuer, snapshot, now)
	parsed, err := jose.ParseSigned(token, []jose.SignatureAlgorithm{jose.ES256})
	if err != nil {
		t.Fatal(err)
	}
	payload, err := parsed.Verify(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := jose.NewSigner(jose.SigningKey{Algorithm: jose.ES256, Key: key}, new(jose.SignerOptions).WithType(leaseType).WithHeader("kid", issuer.ID))
	if err != nil {
		t.Fatal(err)
	}
	variants := []string{
		strings.Replace(string(payload), `"v":1`, `"v":1,"v":1`, 1),
		strings.Replace(string(payload), `"v":1`, `"v":1,"unrecognized":true`, 1),
		strings.Replace(string(payload), `"sequence":"1"`, `"sequence":"01"`, 1),
		strings.Replace(string(payload), `"iat":1793260800`, `"iat":1.7932608e9`, 1),
		strings.Replace(string(payload), `"exp":1793260920`, `"exp":1793260921`, 1),
		strings.Replace(string(payload), `"org.example.client"`, `"org.example.\ud800"`, 1),
	}
	for _, value := range variants {
		if value == string(payload) {
			t.Fatal("mutation fixture did not change")
		}
		signed, err := signer.Sign([]byte(value))
		if err != nil {
			t.Fatal(err)
		}
		bad, err := signed.CompactSerialize()
		if err != nil {
			t.Fatal(err)
		}
		if _, err := VerifyLease(bad, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now, 1); err == nil {
			t.Fatal("signed malformed claims accepted")
		}
	}
	parts := strings.Split(token, ".")
	header := `{"alg":"none","typ":"` + leaseType + `","kid":"` + issuer.ID + `"}`
	parts[0] = base64.RawURLEncoding.EncodeToString([]byte(header))
	if _, err := VerifyLease(strings.Join(parts, "."), &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now, 1); err == nil {
		t.Fatal("algorithm downgrade accepted")
	}
	withDiscovery, _ := jose.NewSigner(jose.SigningKey{Algorithm: jose.ES256, Key: key}, new(jose.SignerOptions).WithType(leaseType).WithHeader("kid", issuer.ID).WithHeader("jku", "https://untrusted.invalid/key"))
	signed, _ := withDiscovery.Sign(payload)
	bad, _ := signed.CompactSerialize()
	if _, err := VerifyLease(bad, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now, 1); err == nil {
		t.Fatal("remote key discovery header accepted")
	}
}
func TestLeaseReplayCannotExtendMonotonicWindow(t *testing.T) {
	issuer, key, snapshot, now := leaseFixture(t)
	token := tokenFixture(t, issuer, snapshot, now)
	first, err := VerifyLease(token, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now, 100)
	if err != nil {
		t.Fatal(err)
	}
	var window LeaseWindow
	accepted, err := window.Accept(first, 100)
	if err != nil {
		t.Fatal(err)
	}
	// Re-verifying the same token with a stalled wall clock must not restart its 120-second duration.
	replayed, err := VerifyLease(token, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now, 100+int64(60*time.Second))
	if err != nil {
		t.Fatal(err)
	}
	same, err := window.Accept(replayed, replayed.AcceptedNanos)
	if err != nil || same.DeadlineNanos != accepted.DeadlineNanos {
		t.Fatal("replay extended the deadline")
	}
	var expired LeaseWindow
	if _, err := expired.Accept(first, first.AcceptedNanos); err != nil {
		t.Fatal(err)
	}
	if _, err := expired.Accept(replayed, first.DeadlineNanos); err == nil {
		t.Fatal("expired replay revived")
	}
	newToken := tokenFixture(t, issuer, snapshot, now.Add(60*time.Second))
	newReceipt, err := VerifyLease(newToken, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now.Add(60*time.Second), replayed.AcceptedNanos)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := window.Accept(newReceipt, newReceipt.AcceptedNanos); err != nil {
		t.Fatal(err)
	}
	if _, err := window.Accept(first, newReceipt.AcceptedNanos+1); err == nil {
		t.Fatal("old sequence accepted after renewal")
	}
	if _, err := window.Accept(newReceipt, newReceipt.AcceptedNanos-1); err == nil {
		t.Fatal("monotonic rollback accepted")
	}
	window.Close()
	if _, err := window.Current(newReceipt.AcceptedNanos); err == nil {
		t.Fatal("closed control generation stayed ready")
	}
	if _, err := window.Accept(newReceipt, newReceipt.AcceptedNanos); err == nil {
		t.Fatal("late reply revived closed control generation")
	}
}
func TestStrictJsonAndDecimalRevision(t *testing.T) {
	for _, input := range []string{`{"active":true,"active":false}`, `{"ACTIVE":true}`, `{"active":true,"unknown":1}`, `{"active":"\ud800"}`, strings.Repeat("[", 17) + strings.Repeat("]", 17)} {
		var result struct {
			Active bool `json:"active"`
		}
		if err := decode([]byte(input), &result); err == nil {
			t.Fatal("noncanonical or ambiguous JSON accepted")
		}
	}
	for _, input := range []string{`1`, `"01"`, `"+1"`, `"-1"`, `"1e2"`, `"18446744073709551616"`} {
		var value Revision
		if decode([]byte(input), &value) == nil {
			t.Fatal("invalid revision accepted")
		}
	}
	var maximum Revision
	if err := decode([]byte(`"18446744073709551615"`), &maximum); err != nil || maximum != Revision(math.MaxUint64) {
		t.Fatal("UInt64 maximum rejected")
	}
}
func TestLateHigherSequenceWithStalledWallClockCannotExtendSignedExpiry(t *testing.T) {
	issuer, key, snapshot, now := leaseFixture(t)
	firstToken := tokenFixture(t, issuer, snapshot, now)
	laterToken := tokenFixture(t, issuer, snapshot, now)
	lastToken := tokenFixture(t, issuer, snapshot, now)
	first, err := VerifyLease(firstToken, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now, 0)
	if err != nil {
		t.Fatal(err)
	}
	var window LeaseWindow
	if _, err = window.Accept(first, 0); err != nil {
		t.Fatal(err)
	}
	late, err := VerifyLease(laterToken, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now, int64(90*time.Second))
	if err != nil {
		t.Fatal(err)
	}
	accepted, err := window.Accept(late, int64(90*time.Second))
	if err != nil || accepted.DeadlineNanos != int64(120*time.Second) {
		t.Fatal("stalled wall clock extended a different, higher-sequence old token")
	}
	if _, err = window.Current(int64(121 * time.Second)); err == nil {
		t.Fatal("signed expiry extended")
	}
	late, err = VerifyLease(lastToken, &key.PublicKey, issuer.ID, issuer.Epoch, snapshot, now, int64(121*time.Second))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = window.Accept(late, int64(121*time.Second)); err == nil {
		t.Fatal("higher unseen sequence revived expired signed time")
	}
}
