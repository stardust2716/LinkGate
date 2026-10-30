package authority

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	bolt "go.etcd.io/bbolt"
)

// This is deliberately not a production witness: tests exercise transaction and
// recovery policy, not external-file durability or hardware rollback resistance.
type memoryWitness struct {
	mu                          sync.Mutex
	state                       WitnessState
	prepareThenFail, commitFail bool
	prepares                    int
}

func (w *memoryWitness) Read() (WitnessState, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	value := w.state
	if value.Prepared != nil {
		copy := *value.Prepared
		value.Prepared = &copy
	}
	return value, nil
}
func (w *memoryWitness) Prepare(old, next Anchor) error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.state.Prepared != nil || w.state.Committed != old {
		return errors.New("fixture mismatch")
	}
	w.prepares++
	copy := next
	w.state.Prepared = &copy
	if w.prepareThenFail {
		return errors.New("private fixture prepare diagnostic")
	}
	return nil
}
func (w *memoryWitness) Commit(next Anchor) error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.commitFail {
		return errors.New("private fixture commit diagnostic")
	}
	if w.state.Prepared == nil || *w.state.Prepared != next {
		return errors.New("fixture mismatch")
	}
	w.state = WitnessState{Committed: next}
	return nil
}

type storeFixture struct {
	s                                       *Store
	db                                      *bolt.DB
	w                                       *memoryWitness
	directory                               string
	now                                     time.Time
	owner, publisher, visitor, server, root string
	scope                                   AccessScope
	grantOperation                          string
	grantOutcome                            Outcome
}

func newStoreFixture(t *testing.T) *storeFixture {
	t.Helper()
	cwd, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	for {
		if _, err := os.Stat(filepath.Join(cwd, "gateway")); err == nil {
			break
		}
		parent := filepath.Dir(cwd)
		if parent == cwd {
			t.Fatal("repository missing")
		}
		cwd = parent
	}
	root := filepath.Join(cwd, "artifacts", "control-authority-tests")
	directory := filepath.Join(root, uuid.NewString())
	if err := os.MkdirAll(directory, 0700); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		absolute, _ := filepath.Abs(directory)
		prefix, _ := filepath.Abs(root)
		if !strings.HasPrefix(absolute, prefix+string(filepath.Separator)) {
			t.Error("unsafe fixture cleanup")
			return
		}
		if err := os.RemoveAll(absolute); err != nil {
			t.Error(err)
		}
	})
	db, err := bolt.Open(filepath.Join(directory, "authority.db"), 0600, &bolt.Options{Timeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	f := &storeFixture{db: db, w: &memoryWitness{}, directory: directory, now: time.Unix(1793260800, 0), owner: strings.Repeat("c", 64), publisher: strings.Repeat("b", 64), visitor: strings.Repeat("a", 64), server: strings.Repeat("d", 64), root: strings.Repeat("e", 64)}
	f.s, err = NewStore(db, f.w, f.server, f.root, f.owner, f.now, true)
	if err != nil {
		t.Fatal(err)
	}
	f.scope = AccessScope{VisitorDevice: f.visitor, VisitorApp: "org.example.client", Target: ServiceScope{PublisherDevice: f.publisher, PublisherApp: "org.example.publisher", Service: "files"}}
	if _, err = f.s.SetDevice(f.owner, uuid.NewString(), f.publisher, 0, Roles{Publish: true}, true, f.now); err != nil {
		t.Fatal(err)
	}
	if _, err = f.s.SetDevice(f.owner, uuid.NewString(), f.visitor, 0, Roles{Access: true}, true, f.now); err != nil {
		t.Fatal(err)
	}
	if _, err = f.s.SetService(f.owner, uuid.NewString(), f.scope.Target, 0, "https", "files.example.invalid", true, f.now); err != nil {
		t.Fatal(err)
	}
	f.grantOperation = uuid.NewString()
	f.grantOutcome, err = f.s.SetGrant(f.owner, f.grantOperation, f.scope, 0, true, f.now)
	if err != nil {
		t.Fatal(err)
	}
	return f
}
func wantCode(t *testing.T, err error, code string) {
	t.Helper()
	if err == nil || Code(err) != code || strings.Contains(err.Error(), "private fixture") {
		t.Fatalf("expected fixed %s, got %v", code, err)
	}
}
func TestOwnerAuthorityScopeReplayAndIsolation(t *testing.T) {
	f := newStoreFixture(t)
	snapshot, err := f.s.Snapshot(f.scope, f.now)
	if err != nil || !validSnapshot(snapshot) {
		t.Fatal(err)
	}
	_, err = f.s.SetDevice(f.visitor, uuid.NewString(), strings.Repeat("f", 64), 0, Roles{Owner: true}, true, f.now)
	wantCode(t, err, "AUTH_REQUIRED")
	_, err = f.s.SetGrant(f.owner, f.grantOperation, f.scope, 1, true, f.now)
	wantCode(t, err, "OPERATION_CONFLICT")
	_, err = f.s.OwnerOperation(f.visitor, f.grantOperation, f.now)
	wantCode(t, err, "OPERATION_UNKNOWN")
	before := f.w.prepares
	old, err := f.s.SetGrant(f.owner, f.grantOperation, f.scope, 0, true, f.now)
	if err != nil || old != f.grantOutcome || f.w.prepares != before {
		t.Fatal("known result was applied again")
	}
	other := f.scope
	other.VisitorApp = "org.example.other"
	_, err = f.s.Snapshot(other, f.now)
	wantCode(t, err, "AUTH_REQUIRED")
	if _, err = f.s.SetGrant(f.owner, uuid.NewString(), f.scope, 1, false, f.now); err != nil {
		t.Fatal(err)
	}
	_, err = f.s.Snapshot(f.scope, f.now)
	wantCode(t, err, "AUTH_REQUIRED")
	old, err = f.s.SetGrant(f.owner, f.grantOperation, f.scope, 0, true, f.now)
	if err != nil || old != f.grantOutcome {
		t.Fatal(err)
	}
	_, err = f.s.Snapshot(f.scope, f.now)
	wantCode(t, err, "AUTH_REQUIRED")
	restarted, err := NewStore(f.db, f.w, f.server, f.root, "", f.now, false)
	if err != nil {
		t.Fatal(err)
	}
	known, err := restarted.OwnerOperation(f.owner, f.grantOperation, f.now)
	if err != nil || known != f.grantOutcome {
		t.Fatal("historical outcome not retained")
	}
}
func TestDeviceAndServiceGenerationChangesCannotReviveGrants(t *testing.T) {
	f := newStoreFixture(t)
	if _, err := f.s.SetDevice(f.owner, uuid.NewString(), f.visitor, 1, Roles{Access: true}, true, f.now); err != nil {
		t.Fatal(err)
	}
	_, err := f.s.Snapshot(f.scope, f.now)
	wantCode(t, err, "AUTH_REQUIRED")
	if _, err = f.s.SetGrant(f.owner, uuid.NewString(), f.scope, 2, true, f.now); err != nil {
		t.Fatal(err)
	}
	if _, err = f.s.SetService(f.owner, uuid.NewString(), f.scope.Target, 1, "https", "files.example.invalid", true, f.now); err != nil {
		t.Fatal(err)
	}
	_, err = f.s.Snapshot(f.scope, f.now)
	wantCode(t, err, "AUTH_REQUIRED")
	if _, err = f.s.SetGrant(f.owner, uuid.NewString(), f.scope, 4, true, f.now); err != nil {
		t.Fatal(err)
	}
	if _, err = f.s.SetDevice(f.owner, uuid.NewString(), f.publisher, 1, Roles{Publish: true}, true, f.now); err != nil {
		t.Fatal(err)
	}
	_, err = f.s.Snapshot(f.scope, f.now)
	wantCode(t, err, "AUTH_REQUIRED")
	if _, err = f.s.SetGrant(f.owner, uuid.NewString(), f.scope, 6, true, f.now); Code(err) != "AUTH_REQUIRED" {
		t.Fatal("stale service generation accepted")
	}
	_, err = f.s.SetDevice(f.owner, uuid.NewString(), f.owner, 1, Roles{}, false, f.now)
	wantCode(t, err, "AUTH_REQUIRED")
}
func TestFullJournalStillAllowsEveryReservedRevocation(t *testing.T) {
	f := newStoreFixture(t)
	f.s.operationLimit = 10
	if _, err := f.s.SetGrant(f.owner, uuid.NewString(), f.scope, 1, true, f.now); err != nil {
		t.Fatal(err)
	}
	_, err := f.s.SetGrant(f.owner, uuid.NewString(), f.scope, 2, true, f.now)
	wantCode(t, err, "RESOURCE_EXHAUSTED")
	current, err := f.s.Snapshot(f.scope, f.now)
	if err != nil || current.Grant.Revision != 2 {
		t.Fatal("capacity rejection changed authority")
	}
	if _, err = f.s.SetGrant(f.owner, uuid.NewString(), f.scope, 2, false, f.now); err != nil {
		t.Fatal(err)
	}
	if _, err = f.s.SetService(f.owner, uuid.NewString(), f.scope.Target, 1, "https", "files.example.invalid", false, f.now); err != nil {
		t.Fatal(err)
	}
	if _, err = f.s.SetDevice(f.owner, uuid.NewString(), f.visitor, 1, Roles{}, false, f.now); err != nil {
		t.Fatal(err)
	}
	if _, err = f.s.SetDevice(f.owner, uuid.NewString(), f.publisher, 1, Roles{}, false, f.now); err != nil {
		t.Fatal(err)
	}
	_, err = f.s.SetDevice(f.owner, uuid.NewString(), strings.Repeat("f", 64), 0, Roles{Access: true}, true, f.now)
	wantCode(t, err, "RESOURCE_EXHAUSTED")
	_, err = f.s.OwnerOperation(f.owner, f.grantOperation, f.now)
	if err != nil {
		t.Fatal("full journal lost old outcome")
	}
}
func TestCommittedDatabaseFinalizesPreparedWitnessWithoutReplay(t *testing.T) {
	f := newStoreFixture(t)
	f.w.commitFail = true
	operation := uuid.NewString()
	_, err := f.s.SetGrant(f.owner, operation, f.scope, 1, false, f.now)
	wantCode(t, err, "STORAGE_UNAVAILABLE")
	_, err = f.s.Snapshot(f.scope, f.now)
	wantCode(t, err, "STORAGE_UNAVAILABLE")
	f.w.commitFail = false
	restarted, err := NewStore(f.db, f.w, f.server, f.root, "", f.now, false)
	if err != nil {
		t.Fatal(err)
	}
	known, err := restarted.OwnerOperation(f.owner, operation, f.now)
	if err != nil || known.Active || known.Revision != 2 {
		t.Fatal("committed revoke outcome missing")
	}
	_, err = restarted.Snapshot(f.scope, f.now)
	wantCode(t, err, "AUTH_REQUIRED")
	before := f.w.prepares
	again, err := restarted.SetGrant(f.owner, operation, f.scope, 1, false, f.now)
	if err != nil || again != known || f.w.prepares != before {
		t.Fatal("recovery repeated mutation")
	}
}
func TestAheadOfDatabaseWitnessDoesNotReauthorizeOldState(t *testing.T) {
	f := newStoreFixture(t)
	f.w.prepareThenFail = true
	_, err := f.s.SetGrant(f.owner, uuid.NewString(), f.scope, 1, false, f.now)
	wantCode(t, err, "STORAGE_UNAVAILABLE")
	f.w.prepareThenFail = false
	_, err = NewStore(f.db, f.w, f.server, f.root, "", f.now, false)
	wantCode(t, err, "STORAGE_UNAVAILABLE")
	_, err = f.s.Snapshot(f.scope, f.now)
	wantCode(t, err, "STORAGE_UNAVAILABLE")
}
func TestDatabaseRestoreAndMissingWitnessFailClosed(t *testing.T) {
	f := newStoreFixture(t)
	backup := filepath.Join(f.directory, "backup.db")
	if err := f.db.View(func(tx *bolt.Tx) error { return tx.CopyFile(backup, 0600) }); err != nil {
		t.Fatal(err)
	}
	if _, err := f.s.SetGrant(f.owner, uuid.NewString(), f.scope, 1, false, f.now); err != nil {
		t.Fatal(err)
	}
	if err := f.db.Close(); err != nil {
		t.Fatal(err)
	}
	saved, err := os.ReadFile(backup)
	if err != nil {
		t.Fatal(err)
	}
	databasePath := filepath.Join(f.directory, "authority.db")
	if err = os.WriteFile(databasePath, saved, 0600); err != nil {
		t.Fatal(err)
	}
	restored, err := bolt.Open(databasePath, 0600, &bolt.Options{Timeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	defer restored.Close()
	_, err = NewStore(restored, f.w, f.server, f.root, "", f.now, false)
	wantCode(t, err, "STORAGE_UNAVAILABLE")
	_, err = NewStore(restored, &memoryWitness{}, f.server, f.root, "", f.now, false)
	wantCode(t, err, "STORAGE_UNAVAILABLE")
}
func TestConcurrentOwnerDecisionsSpendOneSnapshotOnce(t *testing.T) {
	f := newStoreFixture(t)
	var group sync.WaitGroup
	results := make(chan error, 12)
	for i := 0; i < 12; i++ {
		group.Add(1)
		go func() {
			defer group.Done()
			_, err := f.s.SetGrant(f.owner, uuid.NewString(), f.scope, 1, false, f.now)
			results <- err
		}()
	}
	group.Wait()
	close(results)
	success := 0
	for err := range results {
		if err == nil {
			success++
		} else {
			wantCode(t, err, "REVIEW_STALE")
		}
	}
	if success != 1 {
		t.Fatal("concurrent decisions reused the same revision")
	}
}
func TestWitnessLossClockRollbackAndCorruptStateStopAdmission(t *testing.T) {
	t.Run("witness loss", func(t *testing.T) {
		f := newStoreFixture(t)
		f.w.state = WitnessState{}
		_, err := f.s.Snapshot(f.scope, f.now)
		wantCode(t, err, "STORAGE_UNAVAILABLE")
	})
	t.Run("clock rollback", func(t *testing.T) {
		f := newStoreFixture(t)
		_, err := f.s.Snapshot(f.scope, f.now.Add(-121*time.Second))
		wantCode(t, err, "CLOCK_UNTRUSTED")
	})
	t.Run("corrupt grant", func(t *testing.T) {
		f := newStoreFixture(t)
		if err := f.db.Update(func(tx *bolt.Tx) error {
			values, _ := bucket(tx, "grants")
			return values.Put([]byte(scopeKey(f.scope)), []byte(`{"private":"not an authority record"}`))
		}); err != nil {
			t.Fatal(err)
		}
		_, err := f.s.Snapshot(f.scope, f.now)
		wantCode(t, err, "STORAGE_UNAVAILABLE")
	})
	t.Run("missing schema", func(t *testing.T) {
		f := newStoreFixture(t)
		if err := f.db.Update(func(tx *bolt.Tx) error { return tx.DeleteBucket([]byte("operations")) }); err != nil {
			t.Fatal(err)
		}
		_, err := NewStore(f.db, f.w, f.server, f.root, "", f.now, false)
		wantCode(t, err, "STORAGE_UNAVAILABLE")
	})
}
