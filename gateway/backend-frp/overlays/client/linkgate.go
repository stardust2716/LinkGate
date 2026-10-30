package client

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"net"
	"os"
	"strconv"
	"time"
)

func linkgateBridgeEnabled() bool {
	return os.Getenv("LINKGATE_AGENT_PIPE") != "" || os.Getenv("LINKGATE_AGENT_PID") != ""
}
func linkgateBridgeMetadata() (string, int, error) {
	path, number := os.Getenv("LINKGATE_AGENT_PIPE"), os.Getenv("LINKGATE_AGENT_PID")
	pid, err := strconv.Atoi(number)
	if err != nil || pid < 1 || strconv.Itoa(pid) != number || pid != os.Getppid() || path == "" || len(path) > 240 {
		return "", 0, errors.New("AGENT_IDENTITY_REQUIRED")
	}
	return path, pid, nil
}
func linkgateInitialRunID(previous string) (string, error) {
	if previous != "" || !linkgateBridgeEnabled() {
		return previous, nil
	}
	var bytes [16]byte
	if _, err := rand.Read(bytes[:]); err != nil {
		return "", errors.New("AGENT_IDENTITY_REQUIRED")
	}
	return hex.EncodeToString(bytes[:]), nil
}
func linkgateConnect(ctx context.Context) (net.Conn, error) {
	path, pid, err := linkgateBridgeMetadata()
	if err != nil {
		return nil, err
	}
	limit, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	return linkgateDial(limit, path, pid)
}
