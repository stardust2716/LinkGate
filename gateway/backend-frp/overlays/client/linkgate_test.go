package client

import (
	"os"
	"strconv"
	"testing"
)

func TestLinkGateBridgeMetadataCannotFallBack(t *testing.T) {
	t.Setenv("LINKGATE_AGENT_PIPE", "")
	t.Setenv("LINKGATE_AGENT_PID", "")
	if linkgateBridgeEnabled() {
		t.Fatal("bridge mode enabled without Agent metadata")
	}
	t.Setenv("LINKGATE_AGENT_PID", strconv.Itoa(os.Getppid()))
	if !linkgateBridgeEnabled() {
		t.Fatal("partial Agent metadata silently disabled bridge")
	}
	if _, _, err := linkgateBridgeMetadata(); err == nil {
		t.Fatal("partial Agent metadata admitted")
	}
	t.Setenv("LINKGATE_AGENT_PIPE", "owned-fixture")
	t.Setenv("LINKGATE_AGENT_PID", "0")
	if _, _, err := linkgateBridgeMetadata(); err == nil {
		t.Fatal("wrong parent admitted")
	}
	t.Setenv("LINKGATE_AGENT_PID", strconv.Itoa(os.Getppid()))
	_, pid, err := linkgateBridgeMetadata()
	if err != nil || pid != os.Getppid() {
		t.Fatal("actual parent metadata rejected")
	}
}
func TestLinkGateBridgeCreatesInitialRunIdentity(t *testing.T) {
	t.Setenv("LINKGATE_AGENT_PIPE", "owned-fixture")
	first, err := linkgateInitialRunID("")
	if err != nil || len(first) != 32 {
		t.Fatal("initial owned RunID missing")
	}
	second, err := linkgateInitialRunID("")
	if err != nil || first == second {
		t.Fatal("initial owned RunID reused")
	}
	if preserved, err := linkgateInitialRunID(first); err != nil || preserved != first {
		t.Fatal("reconnect changed original RunID")
	}
}
