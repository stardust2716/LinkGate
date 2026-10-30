package authority

import "sync/atomic"

type saturatingCounter struct{ value atomic.Uint64 }

func (c *saturatingCounter) record() {
	for {
		old := c.value.Load()
		if old == ^uint64(0) || c.value.CompareAndSwap(old, old+1) {
			return
		}
	}
}

type controlCounters struct{ issued, validated, checked saturatingCounter }
type ControlStatistics struct {
	LeasesIssued         uint64 `json:"leasesIssued"`
	PublisherValidations uint64 `json:"publisherValidations"`
	AuthorityBatches     uint64 `json:"authorityBatches"`
}

func (s *ControlServer) Statistics() ControlStatistics {
	return ControlStatistics{s.statistics.issued.value.Load(), s.statistics.validated.value.Load(), s.statistics.checked.value.Load()}
}
