package authority

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"math"
	"sync"
	"time"

	bolt "go.etcd.io/bbolt"
)

// The platform host owns database/witness paths, their ACLs and lifecycle. It must
// keep the witness outside authority backups; an in-memory witness is test-only.
type Anchor struct {
	Server     string   `json:"server"`
	Root       string   `json:"root"`
	Generation Revision `json:"generation"`
	Digest     string   `json:"digest"`
}
type WitnessState struct {
	Committed Anchor  `json:"committed"`
	Prepared  *Anchor `json:"prepared"`
}
type Witness interface {
	Read() (WitnessState, error)
	Prepare(previous, next Anchor) error
	Commit(next Anchor) error
}
type metadata struct {
	Schema     int      `json:"schema"`
	Anchor     Anchor   `json:"anchor"`
	MaxUTC     int64    `json:"maxUtc"`
	Operations Revision `json:"operations"`
}
type Outcome struct {
	Kind      string   `json:"kind"`
	Reference string   `json:"reference"`
	Revision  Revision `json:"revision"`
	Active    bool     `json:"active"`
	Affected  int      `json:"affected"`
}
type operation struct {
	Digest string  `json:"digest"`
	Result Outcome `json:"result"`
}

type Store struct {
	db             *bolt.DB
	witness        Witness
	server, root   string
	mu             sync.Mutex
	blocked        bool
	operationLimit int
}

var bucketNames = []string{"meta", "devices", "services", "grants", "operations"}

// NewStore never creates or secures a path. That is the platform host's job.
// Bootstrap is accepted only for an explicitly new, empty database and empty witness.
func NewStore(db *bolt.DB, witness Witness, server, root, bootstrapOwner string, now time.Time, create bool) (*Store, error) {
	if db == nil || db.NoSync || witness == nil || !validKey(server) || !validKey(root) || now.Unix() <= 0 {
		return nil, reject("STORAGE_UNAVAILABLE")
	}
	s := &Store{db: db, witness: witness, server: server, root: root, operationLimit: 1000000}
	state, err := witness.Read()
	if err != nil {
		return nil, reject("STORAGE_UNAVAILABLE")
	}
	if create {
		if !validKey(bootstrapOwner) || state.Committed != (Anchor{}) || state.Prepared != nil {
			return nil, reject("STORAGE_UNAVAILABLE")
		}
		digest := hash([]byte("linkgate.authority.v1\n" + server + "\n" + root + "\n" + bootstrapOwner))
		next := Anchor{Server: server, Root: root, Generation: 1, Digest: digest}
		err = db.Update(func(tx *bolt.Tx) error {
			count := 0
			_ = tx.ForEach(func([]byte, *bolt.Bucket) error { count++; return nil })
			if count != 0 {
				return reject("STORAGE_UNAVAILABLE")
			}
			for _, name := range bucketNames {
				if _, e := tx.CreateBucket([]byte(name)); e != nil {
					return e
				}
			}
			if e := put(tx, "devices", bootstrapOwner, Device{ID: bootstrapOwner, Revision: 1, Active: true, Roles: Roles{Owner: true}}); e != nil {
				return e
			}
			if e := put(tx, "meta", "state", metadata{Schema: 1, Anchor: next, MaxUTC: now.Unix()}); e != nil {
				return e
			}
			return witness.Prepare(Anchor{}, next)
		})
		if err != nil {
			return nil, reject("STORAGE_UNAVAILABLE")
		}
		if witness.Commit(next) != nil {
			return nil, reject("STORAGE_UNAVAILABLE")
		}
	} else {
		err = db.View(func(tx *bolt.Tx) error {
			meta, e := s.meta(tx, now)
			if e != nil {
				return e
			}
			reserved, e := s.reservations(tx)
			if e != nil {
				return e
			}
			operations, e := bucket(tx, "operations")
			if e != nil || Revision(operations.Stats().KeyN) != meta.Operations || int(meta.Operations)+reserved > s.operationLimit {
				return reject("STORAGE_UNAVAILABLE")
			}
			if state.Prepared != nil {
				if meta.Anchor != *state.Prepared {
					return reject("STORAGE_UNAVAILABLE")
				}
				if witness.Commit(meta.Anchor) != nil {
					return reject("STORAGE_UNAVAILABLE")
				}
			} else if meta.Anchor != state.Committed {
				return reject("STORAGE_UNAVAILABLE")
			}
			return nil
		})
		if err != nil {
			return nil, reject("STORAGE_UNAVAILABLE")
		}
	}
	return s, nil
}
func (s *Store) checkWitness(expected Anchor) error {
	state, err := s.witness.Read()
	if err != nil || state.Prepared != nil || state.Committed != expected {
		s.blocked = true
		return reject("STORAGE_UNAVAILABLE")
	}
	return nil
}
func hash(data []byte) string { digest := sha256.Sum256(data); return hex.EncodeToString(digest[:]) }
func scopeKey(scope any) string {
	encoded, err := encode(scope)
	if err != nil {
		return ""
	}
	return hash(encoded)
}
func bucket(tx *bolt.Tx, name string) (*bolt.Bucket, error) {
	value := tx.Bucket([]byte(name))
	if value == nil {
		return nil, reject("STORAGE_UNAVAILABLE")
	}
	return value, nil
}
func put(tx *bolt.Tx, name, key string, value any) error {
	data, err := encode(value)
	if err != nil {
		return err
	}
	values, err := bucket(tx, name)
	if err != nil {
		return err
	}
	return values.Put([]byte(key), data)
}
func get(tx *bolt.Tx, name, key string, value any) (bool, error) {
	values, err := bucket(tx, name)
	if err != nil {
		return false, err
	}
	raw := values.Get([]byte(key))
	if raw == nil {
		return false, nil
	}
	if decode(raw, value) != nil {
		return false, reject("STORAGE_UNAVAILABLE")
	}
	return true, nil
}
func readDevice(tx *bolt.Tx, id string) (Device, error) {
	var value Device
	found, err := get(tx, "devices", id, &value)
	if err != nil {
		return value, err
	}
	if !found {
		return value, reject("AUTH_REQUIRED")
	}
	if value.ID != id || !validKey(id) || value.Revision == 0 || (value.Active && value.Revision == Revision(math.MaxUint64)) {
		return Device{}, reject("STORAGE_UNAVAILABLE")
	}
	return value, nil
}
func readService(tx *bolt.Tx, scope ServiceScope) (Service, error) {
	var value Service
	found, err := get(tx, "services", scopeKey(scope), &value)
	if err != nil {
		return value, err
	}
	if !found {
		return value, reject("AUTH_REQUIRED")
	}
	if value.Scope != scope || !scope.Valid() || value.Revision == 0 || value.PublisherRevision == 0 || !validProtocol(value.Protocol, value.Authority) || (value.Active && value.Revision == Revision(math.MaxUint64)) {
		return Service{}, reject("STORAGE_UNAVAILABLE")
	}
	return value, nil
}
func readGrant(tx *bolt.Tx, scope AccessScope) (Grant, error) {
	var value Grant
	found, err := get(tx, "grants", scopeKey(scope), &value)
	if err != nil {
		return value, err
	}
	if !found {
		return value, reject("AUTH_REQUIRED")
	}
	if value.Scope != scope || !scope.Valid() || value.Revision == 0 || value.VisitorRevision == 0 || value.PublisherRevision == 0 || value.ServiceRevision == 0 || (value.Active && value.Revision == Revision(math.MaxUint64)) {
		return Grant{}, reject("STORAGE_UNAVAILABLE")
	}
	return value, nil
}
func (s *Store) meta(tx *bolt.Tx, now time.Time) (metadata, error) {
	var value metadata
	found, err := get(tx, "meta", "state", &value)
	if err != nil || !found {
		return value, reject("STORAGE_UNAVAILABLE")
	}
	if value.Schema != 1 || value.Anchor.Server != s.server || value.Anchor.Root != s.root || value.Anchor.Generation == 0 || !validKey(value.Anchor.Digest) || value.MaxUTC <= 0 {
		return metadata{}, reject("STORAGE_UNAVAILABLE")
	}
	if value.Operations > Revision(s.operationLimit) || value.Anchor.Generation != value.Operations+1 {
		return metadata{}, reject("STORAGE_UNAVAILABLE")
	}
	count := 0
	if err := tx.ForEach(func(name []byte, _ *bolt.Bucket) error {
		count++
		for _, known := range bucketNames {
			if string(name) == known {
				return nil
			}
		}
		return reject("STORAGE_UNAVAILABLE")
	}); err != nil || count != len(bucketNames) {
		return metadata{}, reject("STORAGE_UNAVAILABLE")
	}
	if now.Unix() < value.MaxUTC-120 {
		return metadata{}, reject("CLOCK_UNTRUSTED")
	}
	return value, nil
}
func requireOwner(tx *bolt.Tx, actor string) error {
	value, err := readDevice(tx, actor)
	if err != nil {
		return err
	}
	if !value.Active || !value.Roles.Owner {
		return reject("AUTH_REQUIRED")
	}
	return nil
}
func (s *Store) reservations(tx *bolt.Tx) (int, error) {
	reserved := 0
	for name, limit := range map[string]int{"devices": 64, "services": 1024, "grants": 4096} {
		values, err := bucket(tx, name)
		if err != nil {
			return 0, err
		}
		count := 0
		err = values.ForEach(func(key, data []byte) error {
			count++
			if count > limit {
				return reject("STORAGE_UNAVAILABLE")
			}
			if data == nil {
				return reject("STORAGE_UNAVAILABLE")
			}
			switch name {
			case "devices":
				value, e := readDevice(tx, string(key))
				if e != nil {
					return e
				}
				if value.Active {
					reserved++
				}
			case "services":
				var value Service
				if decode(data, &value) != nil || scopeKey(value.Scope) != string(key) {
					return reject("STORAGE_UNAVAILABLE")
				}
				checked, e := readService(tx, value.Scope)
				if e != nil {
					return e
				}
				if checked.Active {
					reserved++
				}
			case "grants":
				var value Grant
				if decode(data, &value) != nil || scopeKey(value.Scope) != string(key) {
					return reject("STORAGE_UNAVAILABLE")
				}
				checked, e := readGrant(tx, value.Scope)
				if e != nil {
					return e
				}
				if checked.Active {
					reserved++
				}
			}
			return nil
		})
		if err != nil {
			return 0, err
		}
	}
	return reserved, nil
}
func nextRevision(prior Revision, active bool) (Revision, error) {
	if prior == Revision(math.MaxUint64) || (active && prior >= Revision(math.MaxUint64-1)) {
		return 0, reject("RESOURCE_EXHAUSTED")
	}
	return prior + 1, nil
}

func (s *Store) mutate(actor, operationID, method string, parameters any, now time.Time, change func(*bolt.Tx) (Outcome, error)) (Outcome, error) {
	if !validKey(actor) || !validUUID(operationID) || now.Unix() <= 0 {
		return Outcome{}, reject("INVALID_REQUEST")
	}
	input, err := encode(parameters)
	if err != nil {
		return Outcome{}, reject("INVALID_REQUEST")
	}
	digest := hash(append([]byte(method+"\n"), input...))
	operationKey := actor + "/" + operationID
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.blocked {
		return Outcome{}, reject("STORAGE_UNAVAILABLE")
	}
	var result Outcome
	var next Anchor
	prepared := false
	err = s.db.Update(func(tx *bolt.Tx) error {
		meta, err := s.meta(tx, now)
		if err != nil {
			return err
		}
		if err = s.checkWitness(meta.Anchor); err != nil {
			return err
		}
		var previous operation
		found, err := get(tx, "operations", operationKey, &previous)
		if err != nil {
			return err
		}
		if found {
			if !validKey(previous.Digest) || previous.Result.Kind == "" || previous.Result.Revision == 0 {
				return reject("STORAGE_UNAVAILABLE")
			}
			if previous.Digest != digest {
				return reject("OPERATION_CONFLICT")
			}
			result = previous.Result
			return nil
		}
		if err = requireOwner(tx, actor); err != nil {
			return err
		}
		if meta.Anchor.Generation == Revision(math.MaxUint64) {
			return reject("RESOURCE_EXHAUSTED")
		}
		result, err = change(tx)
		if err != nil {
			return err
		}
		if err = put(tx, "operations", operationKey, operation{Digest: digest, Result: result}); err != nil {
			return err
		}
		reserved, err := s.reservations(tx)
		if err != nil {
			return err
		}
		if int(meta.Operations)+1+reserved > s.operationLimit {
			return reject("RESOURCE_EXHAUSTED")
		}
		output, err := encode(result)
		if err != nil {
			return err
		}
		previousAnchor := meta.Anchor
		next = meta.Anchor
		next.Generation++
		next.Digest = hash([]byte(meta.Anchor.Digest + "\n" + digest + "\n" + string(output)))
		meta.Anchor = next
		meta.Operations++
		if now.Unix() > meta.MaxUTC {
			meta.MaxUTC = now.Unix()
		}
		if err = put(tx, "meta", "state", meta); err != nil {
			return err
		}
		prepared = true
		return s.witness.Prepare(previousAnchor, next)
	})
	if err != nil {
		if prepared {
			s.blocked = true
		}
		var fixed *Error
		if errors.As(err, &fixed) {
			return Outcome{}, fixed
		}
		return Outcome{}, reject("STORAGE_UNAVAILABLE")
	}
	if prepared && s.witness.Commit(next) != nil {
		s.blocked = true
		return Outcome{}, reject("STORAGE_UNAVAILABLE")
	}
	return result, nil
}

func (s *Store) SetDevice(actor, operationID, id string, expected Revision, roles Roles, active bool, now time.Time) (Outcome, error) {
	if !validKey(id) {
		return Outcome{}, reject("INVALID_REQUEST")
	}
	parameters := struct {
		Device   string
		Expected Revision
		Roles    Roles
		Active   bool
	}{id, expected, roles, active}
	return s.mutate(actor, operationID, "device.set", parameters, now, func(tx *bolt.Tx) (Outcome, error) {
		prior, err := readDevice(tx, id)
		exists := err == nil
		if err != nil && Code(err) != "AUTH_REQUIRED" {
			return Outcome{}, err
		}
		if prior.Revision != expected {
			return Outcome{}, reject("REVIEW_STALE")
		}
		if !exists {
			values, _ := bucket(tx, "devices")
			if values.Stats().KeyN >= 64 {
				return Outcome{}, reject("RESOURCE_EXHAUSTED")
			}
		}
		if prior.Active && prior.Roles.Owner && (!active || !roles.Owner) {
			owners := 0
			values, _ := bucket(tx, "devices")
			if err := values.ForEach(func(key, _ []byte) error {
				value, e := readDevice(tx, string(key))
				if e != nil {
					return e
				}
				if value.Active && value.Roles.Owner {
					owners++
				}
				return nil
			}); err != nil {
				return Outcome{}, err
			}
			if owners <= 1 {
				return Outcome{}, reject("AUTH_REQUIRED")
			}
		}
		revision, err := nextRevision(prior.Revision, active)
		if err != nil {
			return Outcome{}, err
		}
		affected, err := invalidate(tx, func(value Service) bool { return value.Scope.PublisherDevice == id }, func(value Grant) bool {
			return value.Scope.VisitorDevice == id || value.Scope.Target.PublisherDevice == id
		})
		if err != nil {
			return Outcome{}, err
		}
		if err = put(tx, "devices", id, Device{ID: id, Revision: revision, Roles: roles, Active: active}); err != nil {
			return Outcome{}, err
		}
		return Outcome{Kind: "device", Reference: id, Revision: revision, Active: active, Affected: affected}, nil
	})
}
func invalidate(tx *bolt.Tx, serviceMatch func(Service) bool, grantMatch func(Grant) bool) (int, error) {
	affected := 0
	services, err := bucket(tx, "services")
	if err != nil {
		return 0, err
	}
	var changedServices []Service
	if err = services.ForEach(func(key, data []byte) error {
		var value Service
		if decode(data, &value) != nil || scopeKey(value.Scope) != string(key) {
			return reject("STORAGE_UNAVAILABLE")
		}
		if value.Active && serviceMatch(value) {
			revision, e := nextRevision(value.Revision, false)
			if e != nil {
				return e
			}
			value.Active = false
			value.Revision = revision
			changedServices = append(changedServices, value)
		}
		return nil
	}); err != nil {
		return 0, err
	}
	for _, value := range changedServices {
		if err = put(tx, "services", scopeKey(value.Scope), value); err != nil {
			return 0, err
		}
		affected++
	}
	grants, err := bucket(tx, "grants")
	if err != nil {
		return 0, err
	}
	var changedGrants []Grant
	if err = grants.ForEach(func(key, data []byte) error {
		var value Grant
		if decode(data, &value) != nil || scopeKey(value.Scope) != string(key) {
			return reject("STORAGE_UNAVAILABLE")
		}
		if value.Active && grantMatch(value) {
			revision, e := nextRevision(value.Revision, false)
			if e != nil {
				return e
			}
			value.Active = false
			value.Revision = revision
			changedGrants = append(changedGrants, value)
		}
		return nil
	}); err != nil {
		return 0, err
	}
	for _, value := range changedGrants {
		if err = put(tx, "grants", scopeKey(value.Scope), value); err != nil {
			return 0, err
		}
		affected++
	}
	return affected, nil
}
func (s *Store) SetService(actor, operationID string, scope ServiceScope, expected Revision, protocol, authority string, active bool, now time.Time) (Outcome, error) {
	if !scope.Valid() || !validProtocol(protocol, authority) {
		return Outcome{}, reject("INVALID_REQUEST")
	}
	parameters := struct {
		Scope               ServiceScope
		Expected            Revision
		Protocol, Authority string
		Active              bool
	}{scope, expected, protocol, authority, active}
	return s.mutate(actor, operationID, "service.set", parameters, now, func(tx *bolt.Tx) (Outcome, error) {
		publisher, err := readDevice(tx, scope.PublisherDevice)
		if err != nil {
			return Outcome{}, err
		}
		if active && (!publisher.Active || !publisher.Roles.Publish) {
			return Outcome{}, reject("AUTH_REQUIRED")
		}
		prior, err := readService(tx, scope)
		exists := err == nil
		if err != nil && Code(err) != "AUTH_REQUIRED" {
			return Outcome{}, err
		}
		if prior.Revision != expected {
			return Outcome{}, reject("REVIEW_STALE")
		}
		if !exists {
			values, _ := bucket(tx, "services")
			if values.Stats().KeyN >= 1024 {
				return Outcome{}, reject("RESOURCE_EXHAUSTED")
			}
			count := 0
			if err := values.ForEach(func(_, data []byte) error {
				var value Service
				if decode(data, &value) != nil {
					return reject("STORAGE_UNAVAILABLE")
				}
				if value.Scope.PublisherDevice == scope.PublisherDevice && value.Scope.PublisherApp == scope.PublisherApp {
					count++
				}
				return nil
			}); err != nil {
				return Outcome{}, err
			}
			if count >= 16 {
				return Outcome{}, reject("RESOURCE_EXHAUSTED")
			}
		}
		revision, err := nextRevision(prior.Revision, active)
		if err != nil {
			return Outcome{}, err
		}
		affected, err := invalidate(tx, func(Service) bool { return false }, func(value Grant) bool { return value.Scope.Target == scope })
		if err != nil {
			return Outcome{}, err
		}
		value := Service{Scope: scope, Revision: revision, PublisherRevision: publisher.Revision, Protocol: protocol, Authority: authority, Active: active}
		if err = put(tx, "services", scopeKey(scope), value); err != nil {
			return Outcome{}, err
		}
		return Outcome{Kind: "service", Reference: scopeKey(scope), Revision: revision, Active: active, Affected: affected}, nil
	})
}
func (s *Store) SetGrant(actor, operationID string, scope AccessScope, expected Revision, active bool, now time.Time) (Outcome, error) {
	if !scope.Valid() {
		return Outcome{}, reject("INVALID_REQUEST")
	}
	parameters := struct {
		Scope    AccessScope
		Expected Revision
		Active   bool
	}{scope, expected, active}
	return s.mutate(actor, operationID, "grant.set", parameters, now, func(tx *bolt.Tx) (Outcome, error) {
		visitor, err := readDevice(tx, scope.VisitorDevice)
		if err != nil {
			return Outcome{}, err
		}
		publisher, err := readDevice(tx, scope.Target.PublisherDevice)
		if err != nil {
			return Outcome{}, err
		}
		service, err := readService(tx, scope.Target)
		if err != nil {
			return Outcome{}, err
		}
		if active && (!visitor.Active || !visitor.Roles.Access || !publisher.Active || !publisher.Roles.Publish || !service.Active || service.PublisherRevision != publisher.Revision) {
			return Outcome{}, reject("AUTH_REQUIRED")
		}
		prior, err := readGrant(tx, scope)
		exists := err == nil
		if err != nil && Code(err) != "AUTH_REQUIRED" {
			return Outcome{}, err
		}
		if prior.Revision != expected {
			return Outcome{}, reject("REVIEW_STALE")
		}
		if !exists {
			values, _ := bucket(tx, "grants")
			if values.Stats().KeyN >= 4096 {
				return Outcome{}, reject("RESOURCE_EXHAUSTED")
			}
		}
		revision, err := nextRevision(prior.Revision, active)
		if err != nil {
			return Outcome{}, err
		}
		value := Grant{Scope: scope, Revision: revision, VisitorRevision: visitor.Revision, PublisherRevision: publisher.Revision, ServiceRevision: service.Revision, Active: active}
		if err = put(tx, "grants", scopeKey(scope), value); err != nil {
			return Outcome{}, err
		}
		return Outcome{Kind: "grant", Reference: scopeKey(scope), Revision: revision, Active: active}, nil
	})
}
func (s *Store) Snapshot(scope AccessScope, now time.Time) (Snapshot, error) {
	if !scope.Valid() {
		return Snapshot{}, reject("INVALID_REQUEST")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.blocked {
		return Snapshot{}, reject("STORAGE_UNAVAILABLE")
	}
	var result Snapshot
	err := s.db.View(func(tx *bolt.Tx) error {
		meta, err := s.meta(tx, now)
		if err != nil {
			return err
		}
		if err = s.checkWitness(meta.Anchor); err != nil {
			return err
		}
		grant, err := readGrant(tx, scope)
		if err != nil {
			return err
		}
		service, err := readService(tx, scope.Target)
		if err != nil {
			return err
		}
		visitor, err := readDevice(tx, scope.VisitorDevice)
		if err != nil {
			return err
		}
		publisher, err := readDevice(tx, scope.Target.PublisherDevice)
		if err != nil {
			return err
		}
		if !visitor.Active || !visitor.Roles.Access || !publisher.Active || !publisher.Roles.Publish || grant.VisitorRevision != visitor.Revision || grant.PublisherRevision != publisher.Revision {
			return reject("AUTH_REQUIRED")
		}
		result = Snapshot{Grant: grant, Service: service}
		if !validSnapshot(result) {
			return reject("AUTH_REQUIRED")
		}
		return nil
	})
	if err != nil {
		var fixed *Error
		if errors.As(err, &fixed) {
			return Snapshot{}, fixed
		}
		return Snapshot{}, reject("STORAGE_UNAVAILABLE")
	}
	return result, nil
}
func (s *Store) OwnerOperation(actor, operationID string, now time.Time) (Outcome, error) {
	if !validKey(actor) || !validUUID(operationID) {
		return Outcome{}, reject("INVALID_REQUEST")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.blocked {
		return Outcome{}, reject("STORAGE_UNAVAILABLE")
	}
	var result Outcome
	err := s.db.View(func(tx *bolt.Tx) error {
		if _, err := s.meta(tx, now); err != nil {
			return err
		}
		var value operation
		found, err := get(tx, "operations", actor+"/"+operationID, &value)
		if err != nil {
			return err
		}
		if !found {
			return reject("OPERATION_UNKNOWN")
		}
		if !validKey(value.Digest) || value.Result.Revision == 0 {
			return reject("STORAGE_UNAVAILABLE")
		}
		result = value.Result
		return nil
	})
	if err != nil {
		var fixed *Error
		if errors.As(err, &fixed) {
			return Outcome{}, fixed
		}
		return Outcome{}, reject("STORAGE_UNAVAILABLE")
	}
	return result, nil
}

// Call only with the device key extracted from an authenticated TLS connection.
func (s *Store) AdmitDevice(id, role string, now time.Time) (Device, error) {
	if !validKey(id) {
		return Device{}, reject("AUTH_REQUIRED")
	}
	switch role {
	case "paired", "owner", "publish", "access", "transport":
	default:
		return Device{}, reject("INVALID_REQUEST")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.blocked {
		return Device{}, reject("STORAGE_UNAVAILABLE")
	}
	var result Device
	err := s.db.View(func(tx *bolt.Tx) error {
		meta, err := s.meta(tx, now)
		if err != nil {
			return err
		}
		if err = s.checkWitness(meta.Anchor); err != nil {
			return err
		}
		result, err = readDevice(tx, id)
		if err != nil {
			return err
		}
		if !result.Active {
			return reject("AUTH_REQUIRED")
		}
		allowed := role == "paired" || (role == "owner" && result.Roles.Owner) || (role == "publish" && result.Roles.Publish) || (role == "access" && result.Roles.Access) || (role == "transport" && (result.Roles.Publish || result.Roles.Access))
		if !allowed {
			return reject("AUTH_REQUIRED")
		}
		return nil
	})
	if err != nil {
		var fixed *Error
		if errors.As(err, &fixed) {
			return Device{}, fixed
		}
		return Device{}, reject("STORAGE_UNAVAILABLE")
	}
	return result, nil
}

// Publication is an internal transport boundary, not an application directory.
// A backend must supply the publisher taken from its actual authenticated peer.
func (s *Store) Publication(scope ServiceScope, now time.Time) (Service, error) {
	if !scope.Valid() {
		return Service{}, reject("AUTH_REQUIRED")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.blocked {
		return Service{}, reject("STORAGE_UNAVAILABLE")
	}
	var result Service
	err := s.db.View(func(tx *bolt.Tx) error {
		meta, err := s.meta(tx, now)
		if err != nil {
			return err
		}
		if err = s.checkWitness(meta.Anchor); err != nil {
			return err
		}
		device, err := readDevice(tx, scope.PublisherDevice)
		if err != nil {
			return err
		}
		if !device.Active || !device.Roles.Publish {
			return reject("AUTH_REQUIRED")
		}
		result, err = readService(tx, scope)
		if err != nil {
			return err
		}
		if !result.Active || result.PublisherRevision != device.Revision {
			return reject("AUTH_REQUIRED")
		}
		return nil
	})
	if err != nil {
		var fixed *Error
		if errors.As(err, &fixed) {
			return Service{}, fixed
		}
		return Service{}, reject("STORAGE_UNAVAILABLE")
	}
	return result, nil
}
