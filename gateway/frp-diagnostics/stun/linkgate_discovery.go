// Copyright 2026 LinkGate contributors. Apache-2.0.
package nathole

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"strconv"
)

var linkgateSTUNBackups = []string{"stun.cloudflare.com:3478", "stun.easyvoip.com:3478"}

type linkgateSTUNReport struct {
	V                  int    `json:"v"`
	Status             string `json:"status"`
	Observations       int    `json:"observations"`
	AttemptedEndpoints int    `json:"attemptedEndpoints"`
	FallbackUsed       bool   `json:"fallbackUsed"`
}

// One UDP socket is retained for all observations. Equal mapped addresses from
// distinct destinations are real observations, not invented duplicate samples.
func discoverWithFallback(primary, backups []string, localAddr string) ([]string, net.Addr, error) {
	if len(primary) == 0 || len(primary)+len(backups) > 4 {
		return nil, nil, fmt.Errorf("stun endpoint limit")
	}
	c, e := listen(localAddr)
	if e != nil {
		return nil, nil, e
	}
	defer c.Close()
	report := linkgateSTUNReport{V: 1, Status: "unreachable"}
	defer func() { b, _ := json.Marshal(report); fmt.Printf("LINKGATE_STUN %s\n", b) }()
	addresses := make([]string, 0, 2)
	seen := map[string]bool{}
	observe := func(endpoint string) *stunResponse {
		remote, e := resolveLinkgateSTUN(endpoint)
		if e != nil {
			return nil
		}
		key := remote.String()
		if seen[key] {
			return nil
		}
		seen[key] = true
		report.AttemptedEndpoints++
		response, e := c.doSTUNRequestAt(remote)
		if e != nil {
			return nil
		}
		host, port, e := net.SplitHostPort(response.externalAddr)
		if e != nil {
			return nil
		}
		mapped := net.ParseIP(host)
		n, e := strconv.Atoi(port)
		if e != nil || n < 1 || n > 65535 || mapped == nil || mapped.To4() == nil || mapped.IsUnspecified() || mapped.IsMulticast() {
			return nil
		}
		addresses = append(addresses, net.JoinHostPort(mapped.To4().String(), strconv.Itoa(n)))
		report.Observations = len(addresses)
		return response
	}
	endpoints := append(append([]string{}, primary...), backups...)
	for index, endpoint := range endpoints {
		if index >= len(primary) {
			report.FallbackUsed = true
		}
		response := observe(endpoint)
		if len(addresses) >= 2 {
			report.Status = "ready"
			return addresses, c.localAddr, nil
		}
		// OTHER/CHANGED-ADDRESS is an alternate server destination, not a second
		// client mapping until a correlated Binding response is actually received.
		if response != nil && response.otherAddr != "" {
			observe(response.otherAddr)
		}
		if len(addresses) >= 2 {
			report.Status = "ready"
			return addresses, c.localAddr, nil
		}
	}
	if len(addresses) > 0 {
		report.Status = "insufficient"
		return nil, c.localAddr, fmt.Errorf("not enough addresses")
	}
	return nil, c.localAddr, fmt.Errorf("no valid STUN observations")
}

func resolveLinkgateSTUN(endpoint string) (*net.UDPAddr, error) {
	host, port, e := net.SplitHostPort(endpoint)
	if e != nil {
		return nil, e
	}
	n, e := strconv.Atoi(port)
	if e != nil || n < 1 || n > 65535 {
		return nil, fmt.Errorf("invalid STUN port")
	}
	ip := net.ParseIP(host)
	if ip == nil {
		ctx, cancel := context.WithTimeout(context.Background(), responseTimeout)
		defer cancel()
		ips, e := net.DefaultResolver.LookupIP(ctx, "ip4", host)
		if e != nil {
			return nil, e
		}
		for _, candidate := range ips {
			if candidate.To4() != nil {
				ip = candidate.To4()
				break
			}
		}
	}
	if ip == nil || ip.To4() == nil || ip.IsUnspecified() || ip.IsMulticast() {
		return nil, fmt.Errorf("STUN IPv4 required")
	}
	// A TUN fake-IP cannot be used as a real physical STUN destination.
	v4 := ip.To4()
	if v4[0] == 198 && (v4[1] == 18 || v4[1] == 19) {
		return nil, fmt.Errorf("STUN fake IP")
	}
	return &net.UDPAddr{IP: v4, Port: n}, nil
}
