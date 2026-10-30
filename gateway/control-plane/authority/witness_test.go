package authority

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/google/uuid"
	bolt "go.etcd.io/bbolt"
)

func TestDurableWitnessRestartAndDatabaseOnlyRestore(t *testing.T) {
	f := newStoreFixture(t)
	witnessPath := filepath.Join(f.directory, "independent-witness.db")
	db, err := bolt.Open(witnessPath, 0600, nil)
	if err != nil {
		t.Fatal(err)
	}
	witness, err := NewBoltWitness(db, true)
	if err != nil {
		t.Fatal(err)
	}
	control, err := bolt.Open(filepath.Join(f.directory, "fresh-authority.db"), 0600, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer control.Close()
	store, err := NewStore(control, witness, f.server, f.root, f.owner, f.now, true)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = store.SetDevice(f.owner, uuid.NewString(), f.publisher, 0, Roles{Publish: true}, true, f.now); err != nil {
		t.Fatal(err)
	}
	if _, err = store.SetDevice(f.owner, uuid.NewString(), f.visitor, 0, Roles{Access: true}, true, f.now); err != nil {
		t.Fatal(err)
	}
	if _, err = store.SetService(f.owner, uuid.NewString(), f.scope.Target, 0, "https", "files.example.invalid", true, f.now); err != nil {
		t.Fatal(err)
	}
	if _, err = store.SetGrant(f.owner, uuid.NewString(), f.scope, 0, true, f.now); err != nil {
		t.Fatal(err)
	}
	backup := filepath.Join(f.directory, "authority-backup.db")
	if err = control.View(func(tx *bolt.Tx) error { return tx.CopyFile(backup, 0600) }); err != nil {
		t.Fatal(err)
	}
	if _, err = store.SetGrant(f.owner, uuid.NewString(), f.scope, 1, false, f.now); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	db, err = bolt.Open(witnessPath, 0600, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	witness, err = NewBoltWitness(db, false)
	if err != nil {
		t.Fatal(err)
	}
	restored, err := bolt.Open(backup, 0600, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer restored.Close()
	_, err = NewStore(restored, witness, f.server, f.root, "", f.now, false)
	wantCode(t, err, "STORAGE_UNAVAILABLE")
	current, err := NewStore(control, witness, f.server, f.root, "", f.now, false)
	if err != nil {
		t.Fatal(err)
	}
	_, err = current.Snapshot(f.scope, f.now)
	wantCode(t, err, "AUTH_REQUIRED")
}
func TestDurableWitnessCompareAndSwapAndMissingRecord(t *testing.T) {
	f := newStoreFixture(t)
	path := filepath.Join(f.directory, "witness.db")
	db, err := bolt.Open(path, 0600, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	witness, err := NewBoltWitness(db, true)
	if err != nil {
		t.Fatal(err)
	}
	state, _ := f.w.Read()
	anchor := state.Committed
	anchor.Generation = 1
	if err = witness.Prepare(Anchor{}, anchor); err != nil {
		t.Fatal(err)
	}
	if err = witness.Commit(anchor); err != nil {
		t.Fatal(err)
	}
	if err = witness.Commit(anchor); err != nil {
		t.Fatal("uncertain commit acknowledgement is not idempotent")
	}
	wantCode(t, witness.Prepare(Anchor{}, anchor), "STORAGE_UNAVAILABLE")
	future := anchor
	future.Generation = 3
	wantCode(t, witness.Prepare(anchor, future), "STORAGE_UNAVAILABLE")
	if err = db.Update(func(tx *bolt.Tx) error { return tx.Bucket(witnessBucket).Delete([]byte("state")) }); err != nil {
		t.Fatal(err)
	}
	_, err = witness.Read()
	wantCode(t, err, "STORAGE_UNAVAILABLE")
	_, err = NewBoltWitness(db, false)
	wantCode(t, err, "STORAGE_UNAVAILABLE")
}
func TestDurableWitnessDetectsRemovedOrReplacedPath(t *testing.T) {
	f := newStoreFixture(t)
	path := filepath.Join(f.directory, "witness.db")
	db, err := bolt.Open(path, 0600, nil)
	if err != nil {
		t.Fatal(err)
	}
	witness, err := NewBoltWitness(db, true)
	if err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	// Close first so Windows sharing rules permit replacement; the held witness
	// must still refuse this different file rather than silently reopening it.
	if err = os.Rename(path, path+".old"); err != nil {
		t.Fatal(err)
	}
	replacement, err := bolt.Open(path, 0600, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer replacement.Close()
	if _, err = NewBoltWitness(replacement, true); err != nil {
		t.Fatal(err)
	}
	_, err = witness.Read()
	wantCode(t, err, "STORAGE_UNAVAILABLE")
}
