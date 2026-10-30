package authority

import (
	"math"
	"os"
	"path/filepath"

	bolt "go.etcd.io/bbolt"
)

type BoltWitness struct {
	db   *bolt.DB
	file os.FileInfo
	path string
}
type witnessRecord struct {
	Version int          `json:"version"`
	State   WitnessState `json:"state"`
}

var witnessBucket = []byte("linkgate-witness-v1")

// The host must open this synchronized database in an independently protected
// location and exclude it from authority backups. This is not a hardware counter.
func NewBoltWitness(db *bolt.DB, create bool) (*BoltWitness, error) {
	if db == nil || db.NoSync {
		return nil, reject("STORAGE_UNAVAILABLE")
	}
	path, err := filepath.Abs(db.Path())
	if err != nil {
		return nil, reject("STORAGE_UNAVAILABLE")
	}
	file, err := os.Lstat(path)
	if err != nil || !file.Mode().IsRegular() || file.Mode()&os.ModeSymlink != 0 {
		return nil, reject("STORAGE_UNAVAILABLE")
	}
	value := &BoltWitness{db: db, file: file, path: path}
	if create {
		err = db.Update(func(tx *bolt.Tx) error {
			count := 0
			_ = tx.ForEach(func([]byte, *bolt.Bucket) error { count++; return nil })
			if count != 0 {
				return reject("STORAGE_UNAVAILABLE")
			}
			values, e := tx.CreateBucket(witnessBucket)
			if e != nil {
				return e
			}
			data, e := encode(witnessRecord{Version: 1})
			if e != nil {
				return e
			}
			return values.Put([]byte("state"), data)
		})
		if err != nil {
			return nil, reject("STORAGE_UNAVAILABLE")
		}
	}
	if _, err = value.Read(); err != nil {
		return nil, err
	}
	return value, nil
}
func validAnchor(value Anchor) bool {
	return validKey(value.Server) && validKey(value.Root) && validKey(value.Digest) && value.Generation > 0
}
func nextAnchor(old, next Anchor) bool {
	if !validAnchor(next) {
		return false
	}
	if old == (Anchor{}) {
		return next.Generation == 1
	}
	return validAnchor(old) && old.Generation < Revision(math.MaxUint64) && next.Generation == old.Generation+1 && next.Server == old.Server && next.Root == old.Root && next.Digest != old.Digest
}
func (w *BoltWitness) checkFile() error {
	current, err := os.Lstat(w.path)
	if err != nil || !current.Mode().IsRegular() || current.Mode()&os.ModeSymlink != 0 || !os.SameFile(w.file, current) || w.db.NoSync {
		return reject("STORAGE_UNAVAILABLE")
	}
	return nil
}
func witnessState(tx *bolt.Tx) (WitnessState, error) {
	count := 0
	if err := tx.ForEach(func(name []byte, _ *bolt.Bucket) error {
		count++
		if string(name) != string(witnessBucket) {
			return reject("STORAGE_UNAVAILABLE")
		}
		return nil
	}); err != nil || count != 1 {
		return WitnessState{}, reject("STORAGE_UNAVAILABLE")
	}
	values := tx.Bucket(witnessBucket)
	if values == nil {
		return WitnessState{}, reject("STORAGE_UNAVAILABLE")
	}
	var record witnessRecord
	if decode(values.Get([]byte("state")), &record) != nil || record.Version != 1 {
		return WitnessState{}, reject("STORAGE_UNAVAILABLE")
	}
	if record.State.Committed != (Anchor{}) && !validAnchor(record.State.Committed) {
		return WitnessState{}, reject("STORAGE_UNAVAILABLE")
	}
	if record.State.Prepared != nil && !nextAnchor(record.State.Committed, *record.State.Prepared) {
		return WitnessState{}, reject("STORAGE_UNAVAILABLE")
	}
	return record.State, nil
}
func writeWitness(tx *bolt.Tx, state WitnessState) error {
	values := tx.Bucket(witnessBucket)
	if values == nil {
		return reject("STORAGE_UNAVAILABLE")
	}
	data, err := encode(witnessRecord{Version: 1, State: state})
	if err != nil {
		return err
	}
	return values.Put([]byte("state"), data)
}
func (w *BoltWitness) Read() (WitnessState, error) {
	if w.checkFile() != nil {
		return WitnessState{}, reject("STORAGE_UNAVAILABLE")
	}
	var result WitnessState
	err := w.db.View(func(tx *bolt.Tx) error { var e error; result, e = witnessState(tx); return e })
	if err != nil {
		return WitnessState{}, reject("STORAGE_UNAVAILABLE")
	}
	return result, nil
}
func (w *BoltWitness) Prepare(previous, next Anchor) error {
	if w.checkFile() != nil || !nextAnchor(previous, next) {
		return reject("STORAGE_UNAVAILABLE")
	}
	err := w.db.Update(func(tx *bolt.Tx) error {
		state, err := witnessState(tx)
		if err != nil {
			return err
		}
		if state.Prepared != nil || state.Committed != previous {
			return reject("STORAGE_UNAVAILABLE")
		}
		state.Prepared = &next
		return writeWitness(tx, state)
	})
	if err != nil {
		return reject("STORAGE_UNAVAILABLE")
	}
	return nil
}
func (w *BoltWitness) Commit(next Anchor) error {
	if w.checkFile() != nil || !validAnchor(next) {
		return reject("STORAGE_UNAVAILABLE")
	}
	err := w.db.Update(func(tx *bolt.Tx) error {
		state, err := witnessState(tx)
		if err != nil {
			return err
		}
		if state.Prepared == nil && state.Committed == next {
			return nil
		}
		if state.Prepared == nil || *state.Prepared != next {
			return reject("STORAGE_UNAVAILABLE")
		}
		return writeWitness(tx, WitnessState{Committed: next})
	})
	if err != nil {
		return reject("STORAGE_UNAVAILABLE")
	}
	return nil
}
