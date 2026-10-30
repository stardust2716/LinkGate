package authority

import (
	"context"
	"crypto/tls"
	"encoding/binary"
	"io"
	"net"
	"regexp"
	"sync"
	"time"
)

const TransportALPN = "linkgate-frp/1"
const transportPeerLimit = 2048
const transportControlLimit = 128
const transportPublicationLimit = 1024
const transportAuthorizationLimit = 1024

var transportRun = regexp.MustCompile(`^[a-zA-Z0-9_-]{8,64}$`)

type agentScope struct {
	Kind        string `json:"kind"`
	Version     int    `json:"apiVersion"`
	Application string `json:"applicationId"`
	Role        string `json:"role"`
}

// TransportGate is the authority shared by the actual patched backend paths.
// Private object fields prevent requests from manufacturing an authenticated peer.
type TransportGate struct {
	identity       *DeviceIdentity
	store          *Store
	clock          func() time.Time
	tls            *tls.Config
	mu             sync.Mutex
	peers          map[*TransportPeer]struct{}
	controls       map[string]*TransportControl
	publications   map[string]*TransportPublication
	authorizations map[*TransportAuthorization]struct{}
	closed         bool
	handshakes     chan struct{}
	requests       chan struct{}
	visits         chan struct{}
	lifetime       context.Context
	cancel         context.CancelFunc
	workers        sync.WaitGroup
	stopped        chan struct{}
	clockMu        sync.Mutex
	lastMono       time.Time
	effectiveUTC   time.Time
}

func NewTransportGate(identity *DeviceIdentity, store *Store, clock func() time.Time) (*TransportGate, error) {
	if identity == nil || identity.Root == nil || identity.Leaf == nil || store == nil || clock == nil || identity.ID != store.server || hash(identity.Root.Raw) != store.root || validateDeviceProfile(identity.Leaf, identity.Root, clock()) != nil {
		return nil, reject("IDENTITY_CHANGED")
	}
	ctx, cancel := context.WithCancel(context.Background())
	g := &TransportGate{identity: identity, store: store, clock: clock, peers: make(map[*TransportPeer]struct{}), controls: make(map[string]*TransportControl), publications: make(map[string]*TransportPublication), authorizations: make(map[*TransportAuthorization]struct{}), stopped: make(chan struct{}), handshakes: make(chan struct{}, 32), requests: make(chan struct{}, 32), visits: make(chan struct{}, 32), lifetime: ctx, cancel: cancel, lastMono: time.Now(), effectiveUTC: clock()}
	configuration, err := identity.ServerTLS(func(id string) bool { _, err := store.AdmitDevice(id, "transport", g.now()); return err == nil }, g.now)
	if err != nil {
		cancel()
		return nil, err
	}
	configuration.NextProtos = []string{TransportALPN}
	g.tls = configuration
	g.workers.Add(2)
	go g.expire()
	go g.recheck()
	go func() { g.workers.Wait(); close(g.stopped) }()
	return g, nil
}

// The wall-clock floor advances at least as fast as actual monotonic time.
func (g *TransportGate) now() time.Time {
	g.clockMu.Lock()
	defer g.clockMu.Unlock()
	mono := time.Now()
	floor := g.effectiveUTC.Add(mono.Sub(g.lastMono))
	wall := g.clock()
	if wall.After(floor) {
		g.effectiveUTC = wall
	} else {
		g.effectiveUTC = floor
	}
	g.lastMono = mono
	return g.effectiveUTC
}

// WriteTransportScope is used only by the owning Agent, outside frpc bytes.
func WriteTransportScope(writer io.Writer, application, role string) error {
	if !validApp(application) || (role != "publish" && role != "access") {
		return reject("INVALID_REQUEST")
	}
	value, err := encode(agentScope{"linkgate.bridge", 1, application, role})
	if err != nil || len(value) > 4096 {
		return reject("INVALID_REQUEST")
	}
	var header [4]byte
	binary.LittleEndian.PutUint32(header[:], uint32(len(value)))
	for _, part := range [][]byte{header[:], value} {
		for len(part) > 0 {
			n, e := writer.Write(part)
			if e != nil {
				return e
			}
			if n <= 0 || n > len(part) {
				return io.ErrShortWrite
			}
			part = part[n:]
		}
	}
	return nil
}

type TransportPeer struct {
	gate              *TransportGate
	device            string
	application, role string
	revision          Revision
	certificateExpiry time.Time
	deadline          time.Time
	connection        *TransportConn
	raw               net.Conn
	bound             *TransportControl
	done              chan struct{}
	once              sync.Once
}

func (p *TransportPeer) Namespace() string {
	return TransportNamespace(p.device, p.application, p.role)
}
func (p *TransportPeer) Application() string   { return p.application }
func (p *TransportPeer) Role() string          { return p.role }
func (p *TransportPeer) Done() <-chan struct{} { return p.done }
func (p *TransportPeer) Close() {
	p.once.Do(func() {
		close(p.done)
		_ = p.raw.Close()
		p.gate.mu.Lock()
		delete(p.gate.peers, p)
		p.gate.mu.Unlock()
	})
}

type TransportConn struct {
	net.Conn
	peer *TransportPeer
}

func (c *TransportConn) Peer() *TransportPeer { return c.peer }
func (c *TransportConn) Close() error         { c.peer.Close(); return nil }

// Accept performs the actual server handshake itself; it does not accept a
// caller-provided ConnectionState or an asserted device ID as authentication.
func (g *TransportGate) Accept(ctx context.Context, raw net.Conn) (*TransportConn, error) {
	if raw == nil {
		return nil, reject("AUTH_REQUIRED")
	}
	fail := func() (*TransportConn, error) { _ = raw.Close(); return nil, reject("AUTH_REQUIRED") }
	select {
	case g.handshakes <- struct{}{}:
		defer func() { <-g.handshakes }()
	default:
		_ = raw.Close()
		return nil, reject("RESOURCE_EXHAUSTED")
	}
	g.mu.Lock()
	busy := g.closed || len(g.peers) >= transportPeerLimit
	g.mu.Unlock()
	if busy {
		_ = raw.Close()
		return nil, reject("RESOURCE_EXHAUSTED")
	}
	limit, cancel := context.WithTimeout(g.lifetime, 3*time.Second)
	stopCaller := context.AfterFunc(ctx, cancel)
	stopClose := context.AfterFunc(limit, func() { _ = raw.Close() })
	defer func() { stopClose(); stopCaller(); cancel() }()
	secure := tls.Server(raw, g.tls)
	_ = raw.SetDeadline(time.Now().Add(3 * time.Second))
	if secure.HandshakeContext(limit) != nil {
		return fail()
	}
	state := secure.ConnectionState()
	id, err := HandshakeDevice(state)
	if err != nil || state.NegotiatedProtocol != TransportALPN {
		return fail()
	}
	var header [4]byte
	if _, err = io.ReadFull(secure, header[:]); err != nil {
		return fail()
	}
	size := binary.LittleEndian.Uint32(header[:])
	if size < 1 || size > 4096 {
		return fail()
	}
	bytes := make([]byte, size)
	if _, err = io.ReadFull(secure, bytes); err != nil {
		return fail()
	}
	var scope agentScope
	if exact(bytes, &scope, "kind", "apiVersion", "applicationId", "role") != nil || scope.Kind != "linkgate.bridge" || scope.Version != 1 || !validApp(scope.Application) || (scope.Role != "publish" && scope.Role != "access") {
		return fail()
	}
	device, err := g.store.AdmitDevice(id, scope.Role, g.now())
	if err != nil {
		return fail()
	}
	bound := state.PeerCertificates[0].NotAfter
	if g.identity.Leaf.NotAfter.Before(bound) {
		bound = g.identity.Leaf.NotAfter
	}
	if g.identity.Root.NotAfter.Before(bound) {
		bound = g.identity.Root.NotAfter
	}
	peer := &TransportPeer{gate: g, device: id, application: scope.Application, role: scope.Role, revision: device.Revision, certificateExpiry: bound, raw: raw, deadline: time.Now().Add(10 * time.Second), done: make(chan struct{})}
	conn := &TransportConn{Conn: secure, peer: peer}
	peer.connection = conn
	g.mu.Lock()
	if g.closed || limit.Err() != nil || len(g.peers) >= transportPeerLimit {
		g.mu.Unlock()
		return fail()
	}
	g.peers[peer] = struct{}{}
	g.mu.Unlock()
	_ = raw.SetDeadline(time.Time{})
	return conn, nil
}

func TransportNamespace(device, application, role string) string {
	if !validKey(device) || !validApp(application) || (role != "publish" && role != "access") {
		return ""
	}
	return "lg1_" + hash([]byte("namespace\n"+device+"\n"+application+"\n"+role))
}
func TransportProxyName(scope ServiceScope, kind string) string {
	if !scope.Valid() || (kind != "stcp" && kind != "xtcp") {
		return ""
	}
	return TransportNamespace(scope.PublisherDevice, scope.PublisherApp, "publish") + ".lg1_" + hash([]byte("service\n"+scope.PublisherDevice+"\n"+scope.PublisherApp+"\n"+scope.Service)) + "_" + kind
}

type TransportControl struct {
	gate      *TransportGate
	peer      *TransportPeer
	run       string
	done      chan struct{}
	once      sync.Once
	mu        sync.Mutex
	heartbeat time.Time
	calls     chan struct{}
	waits     chan struct{}
}

func (c *TransportControl) Namespace() string     { return c.peer.Namespace() }
func (c *TransportControl) Done() <-chan struct{} { return c.done }
func (c *TransportControl) Role() string          { return c.peer.role }
func (c *TransportControl) Close() {
	c.once.Do(func() {
		close(c.done)
		c.peer.Close()
		c.gate.mu.Lock()
		if c.gate.controls[c.run] == c {
			delete(c.gate.controls, c.run)
		}
		c.gate.mu.Unlock()
	})
}
func (c *TransportControl) alive() bool {
	select {
	case <-c.done:
		return false
	case <-c.peer.done:
		return false
	default:
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	return time.Since(c.heartbeat) < 3*time.Second && c.gate.now().Before(c.peer.certificateExpiry)
}
func (g *TransportGate) peerCurrent(p *TransportPeer) bool {
	if p == nil || p.gate != g {
		return false
	}
	select {
	case <-p.done:
		return false
	default:
	}
	if !g.now().Before(p.certificateExpiry) {
		return false
	}
	device, err := g.store.AdmitDevice(p.device, p.role, g.now())
	return err == nil && device.Revision == p.revision
}
func (g *TransportGate) Login(p *TransportPeer, run, user string) (*TransportControl, error) {
	if !transportRun.MatchString(run) || !g.peerCurrent(p) || user != p.Namespace() {
		return nil, reject("AUTH_REQUIRED")
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.closed {
		return nil, reject("AUTH_REQUIRED")
	}
	if len(g.controls) >= transportControlLimit {
		return nil, reject("RESOURCE_EXHAUSTED")
	}
	if _, exists := g.controls[run]; exists || p.bound != nil {
		return nil, reject("AUTH_REQUIRED")
	}
	c := &TransportControl{gate: g, peer: p, run: run, done: make(chan struct{}), heartbeat: time.Now(), calls: make(chan struct{}, 8), waits: make(chan struct{}, 8)}
	g.controls[run] = c
	p.bound = c
	return c, nil
}
func (g *TransportGate) Heartbeat(c *TransportControl) error {
	if c == nil || c.gate != g || !c.alive() || !g.peerCurrent(c.peer) {
		return reject("AUTH_REQUIRED")
	}
	c.mu.Lock()
	c.heartbeat = time.Now()
	c.mu.Unlock()
	return nil
}

// Hole-punch visitor waits use a separate bounded lane so they cannot starve
// the publisher replies or control heartbeats needed to finish those waits.
func (g *TransportGate) Request(c *TransportControl, visitor bool) (func(), error) {
	if c == nil || c.gate != g || !c.alive() {
		return nil, reject("AUTH_REQUIRED")
	}
	lane, local := g.requests, c.calls
	if visitor {
		lane, local = g.visits, c.waits
	}
	select {
	case lane <- struct{}{}:
	default:
		return nil, reject("RESOURCE_EXHAUSTED")
	}
	select {
	case local <- struct{}{}:
	default:
		<-lane
		return nil, reject("RESOURCE_EXHAUSTED")
	}
	var once sync.Once
	return func() { once.Do(func() { <-local; <-lane }) }, nil
}

func (g *TransportGate) Bind(p *TransportPeer, run string) (*TransportControl, error) {
	if run == "" || !g.peerCurrent(p) {
		return nil, reject("AUTH_REQUIRED")
	}
	g.mu.Lock()
	c := g.controls[run]
	g.mu.Unlock()
	if c == nil || !c.alive() || c.peer.device != p.device || c.peer.application != p.application || c.peer.role != p.role || c.peer.revision != p.revision {
		return nil, reject("AUTH_REQUIRED")
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.controls[run] != c || (p.bound != nil && p.bound != c) {
		return nil, reject("AUTH_REQUIRED")
	}
	p.bound = c
	return c, nil
}

type TransportPublication struct {
	gate       *TransportGate
	control    *TransportControl
	service    Service
	kind, name string
	done       chan struct{}
	once       sync.Once
}

func (p *TransportPublication) Name() string          { return p.name }
func (p *TransportPublication) Done() <-chan struct{} { return p.done }
func (p *TransportPublication) Close() {
	p.once.Do(func() {
		close(p.done)
		p.gate.mu.Lock()
		if p.gate.publications[p.name] == p {
			delete(p.gate.publications, p.name)
		}
		p.gate.mu.Unlock()
	})
}
func (p *TransportPublication) alive() bool {
	select {
	case <-p.done:
		return false
	default:
		return p.control.alive()
	}
}
func (g *TransportGate) Publish(c *TransportControl, service, kind, name string) (*TransportPublication, error) {
	if c == nil || c.gate != g || c.peer.role != "publish" || !c.alive() || !g.peerCurrent(c.peer) {
		return nil, reject("AUTH_REQUIRED")
	}
	scope := ServiceScope{PublisherDevice: c.peer.device, PublisherApp: c.peer.application, Service: service}
	expected := TransportProxyName(scope, kind)
	if expected == "" || name != expected {
		return nil, reject("AUTH_REQUIRED")
	}
	current, err := g.store.Publication(scope, g.now())
	if err != nil || current.PublisherRevision != c.peer.revision {
		return nil, reject("AUTH_REQUIRED")
	}
	p := &TransportPublication{gate: g, control: c, service: current, kind: kind, name: name, done: make(chan struct{})}
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.closed || len(g.publications) >= transportPublicationLimit {
		return nil, reject("RESOURCE_EXHAUSTED")
	}
	if _, exists := g.publications[name]; exists {
		return nil, reject("AUTH_REQUIRED")
	}
	g.publications[name] = p
	return p, nil
}
func (g *TransportGate) Unpublish(c *TransportControl, name string) {
	g.mu.Lock()
	p := g.publications[name]
	g.mu.Unlock()
	if p != nil && p.control == c {
		p.Close()
	}
}
func (g *TransportGate) Publisher(c *TransportControl, name string) error {
	g.mu.Lock()
	p := g.publications[name]
	g.mu.Unlock()
	if p == nil || p.control != c || !p.alive() || !g.peerCurrent(c.peer) {
		return reject("AUTH_REQUIRED")
	}
	current, err := g.store.Publication(p.service.Scope, g.now())
	if err != nil || current != p.service {
		return reject("AUTH_REQUIRED")
	}
	return nil
}

type TransportAuthorization struct {
	gate        *TransportGate
	visitor     *TransportControl
	publication *TransportPublication
	snapshot    Snapshot
	expires     time.Time
	mu          sync.Mutex
	done        chan struct{}
	once        sync.Once
}

func (a *TransportAuthorization) Done() <-chan struct{}    { return a.done }
func (a *TransportAuthorization) VisitorNamespace() string { return a.visitor.Namespace() }
func (a *TransportAuthorization) PublisherNamespace() string {
	return a.publication.control.Namespace()
}
func (a *TransportAuthorization) ProxyName() string { return a.publication.name }
func (a *TransportAuthorization) Kind() string      { return a.publication.kind }
func (a *TransportAuthorization) Alive() bool {
	select {
	case <-a.done:
		return false
	default:
	}
	a.mu.Lock()
	deadline := a.expires
	a.mu.Unlock()
	return time.Now().Before(deadline) && a.visitor.alive() && a.publication.alive()
}
func (a *TransportAuthorization) Close() {
	a.once.Do(func() { close(a.done); a.gate.mu.Lock(); delete(a.gate.authorizations, a); a.gate.mu.Unlock() })
}
func (g *TransportGate) Visit(c *TransportControl, name, kind string) (*TransportAuthorization, error) {
	if c == nil || c.gate != g || c.peer.role != "access" || !c.alive() || !g.peerCurrent(c.peer) {
		return nil, reject("AUTH_REQUIRED")
	}
	g.mu.Lock()
	p := g.publications[name]
	g.mu.Unlock()
	if p == nil || p.kind != kind || !p.alive() {
		return nil, reject("AUTH_REQUIRED")
	}
	began := time.Now()
	scope := AccessScope{VisitorDevice: c.peer.device, VisitorApp: c.peer.application, Target: p.service.Scope}
	snapshot, err := g.store.Snapshot(scope, g.now())
	if err != nil || snapshot.Service != p.service || snapshot.Grant.VisitorRevision != c.peer.revision {
		return nil, reject("AUTH_REQUIRED")
	}
	a := &TransportAuthorization{gate: g, visitor: c, publication: p, snapshot: snapshot, expires: began.Add(120 * time.Second), done: make(chan struct{})}
	g.mu.Lock()
	defer g.mu.Unlock()
	if !a.Alive() {
		return nil, reject("AUTH_REQUIRED")
	}
	if g.closed || len(g.authorizations) >= transportAuthorizationLimit {
		return nil, reject("RESOURCE_EXHAUSTED")
	}
	g.authorizations[a] = struct{}{}
	return a, nil
}

// Renew extends the bounded backend lease only after a fresh, unchanged
// authority read. The independent expiry path can close it during blocked I/O.
func (g *TransportGate) Renew(a *TransportAuthorization) error {
	began := time.Now()
	if a == nil || a.gate != g || !a.Alive() {
		return reject("AUTH_REQUIRED")
	}
	snapshot, err := g.store.Snapshot(a.snapshot.Grant.Scope, g.now())
	if err != nil || snapshot != a.snapshot || !a.Alive() {
		a.Close()
		return reject("AUTH_REQUIRED")
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	select {
	case <-a.done:
		return reject("AUTH_REQUIRED")
	default:
	}
	if !time.Now().Before(a.expires) {
		return reject("AUTH_REQUIRED")
	}
	a.expires = began.Add(120 * time.Second)
	return nil
}

func (g *TransportGate) expire() {
	defer g.workers.Done()
	ticker := time.NewTicker(100 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-g.lifetime.Done():
			return
		case <-ticker.C:
			g.mu.Lock()
			peers := make([]*TransportPeer, 0, len(g.peers))
			for p := range g.peers {
				peers = append(peers, p)
			}
			controls := make([]*TransportControl, 0, len(g.controls))
			for _, c := range g.controls {
				controls = append(controls, c)
			}
			publications := make([]*TransportPublication, 0, len(g.publications))
			for _, p := range g.publications {
				publications = append(publications, p)
			}
			authorizations := make([]*TransportAuthorization, 0, len(g.authorizations))
			for a := range g.authorizations {
				authorizations = append(authorizations, a)
			}
			g.mu.Unlock()
			now := g.now()
			for _, p := range peers {
				g.mu.Lock()
				bound := p.bound
				g.mu.Unlock()
				if !now.Before(p.certificateExpiry) || (bound == nil && time.Now().After(p.deadline)) || (bound != nil && !bound.alive()) {
					p.Close()
				}
			}
			for _, c := range controls {
				if !c.alive() {
					c.Close()
				}
			}
			for _, p := range publications {
				if !p.alive() {
					p.Close()
				}
			}
			for _, a := range authorizations {
				if !a.Alive() {
					a.Close()
				}
			}
		}
	}
}
func (g *TransportGate) recheck() {
	defer g.workers.Done()
	ticker := time.NewTicker(500 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-g.lifetime.Done():
			return
		case <-ticker.C:
			g.mu.Lock()
			controls := make([]*TransportControl, 0, len(g.controls))
			for _, c := range g.controls {
				controls = append(controls, c)
			}
			publications := make([]*TransportPublication, 0, len(g.publications))
			for _, p := range g.publications {
				publications = append(publications, p)
			}
			authorizations := make([]*TransportAuthorization, 0, len(g.authorizations))
			for a := range g.authorizations {
				authorizations = append(authorizations, a)
			}
			g.mu.Unlock()
			for _, c := range controls {
				if !g.peerCurrent(c.peer) {
					c.Close()
				}
			}
			for _, p := range publications {
				if g.Publisher(p.control, p.name) != nil {
					p.Close()
				}
			}
			for _, a := range authorizations {
				if g.Renew(a) != nil {
					a.Close()
				}
			}
		}
	}
}
func (g *TransportGate) Close() {
	g.mu.Lock()
	if g.closed {
		g.mu.Unlock()
		return
	}
	g.closed = true
	peers := make([]*TransportPeer, 0, len(g.peers))
	for p := range g.peers {
		peers = append(peers, p)
	}
	controls := make([]*TransportControl, 0, len(g.controls))
	for _, c := range g.controls {
		controls = append(controls, c)
	}
	authorizations := make([]*TransportAuthorization, 0, len(g.authorizations))
	for a := range g.authorizations {
		authorizations = append(authorizations, a)
	}
	publications := make([]*TransportPublication, 0, len(g.publications))
	for _, p := range g.publications {
		publications = append(publications, p)
	}
	g.mu.Unlock()
	g.cancel()
	for _, p := range peers {
		p.Close()
	}
	for _, c := range controls {
		c.Close()
	}
	for _, a := range authorizations {
		a.Close()
	}
	for _, p := range publications {
		p.Close()
	}
}
func (g *TransportGate) WaitClosed(ctx context.Context) error {
	select {
	case <-g.stopped:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

type TransportStatistics struct {
	Peers          int `json:"peers"`
	Controls       int `json:"controls"`
	Publications   int `json:"publications"`
	Authorizations int `json:"authorizations"`
}

func (g *TransportGate) Statistics() TransportStatistics {
	g.mu.Lock()
	defer g.mu.Unlock()
	return TransportStatistics{len(g.peers), len(g.controls), len(g.publications), len(g.authorizations)}
}
