package nathole

import (
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"os"
)

var linkgateFakeIP = netip.MustParsePrefix("198.18.0.0/15")

func linkgateSTUNEndpoint(value string) (netip.AddrPort, error) {
	endpoint, err := netip.ParseAddrPort(value)
	if err != nil || !endpoint.Addr().Is4() || endpoint.String() != value || endpoint.Port() == 0 || endpoint.Addr().IsUnspecified() || endpoint.Addr().IsMulticast() || endpoint.Addr().IsLinkLocalUnicast() || endpoint.Addr() == netip.MustParseAddr("255.255.255.255") || linkgateFakeIP.Contains(endpoint.Addr()) {
		return netip.AddrPort{}, errors.New("STUN_CONFIG_INVALID")
	}
	return endpoint, nil
}
func linkgateSTUNEndpoints(raw string) ([]netip.AddrPort, error) {
	if len(raw) < 2 || len(raw) > 512 {
		return nil, errors.New("STUN_CONFIG_INVALID")
	}
	var values []string
	if json.Unmarshal([]byte(raw), &values) != nil || len(values) < 1 || len(values) > 4 {
		return nil, errors.New("STUN_CONFIG_INVALID")
	}
	result := make([]netip.AddrPort, 0, len(values))
	seen := make(map[netip.AddrPort]bool)
	for _, value := range values {
		endpoint, err := linkgateSTUNEndpoint(value)
		if err != nil || seen[endpoint] {
			return nil, errors.New("STUN_CONFIG_INVALID")
		}
		seen[endpoint] = true
		result = append(result, endpoint)
	}
	return result, nil
}
func linkgateDiscoveryEnabled() bool {
	_, configured := os.LookupEnv("LINKGATE_STUN_ENDPOINTS")
	return configured || os.Getenv("LINKGATE_AGENT_PIPE") != "" || os.Getenv("LINKGATE_AGENT_PID") != ""
}
func linkgateDiscover(raw, localAddr string) ([]string, net.Addr, error) {
	endpoints, err := linkgateSTUNEndpoints(raw)
	if err != nil {
		return nil, nil, err
	}
	connection, err := listen(localAddr)
	if err != nil {
		return nil, nil, errors.New("STUN_SOCKET_UNAVAILABLE")
	}
	defer connection.Close()
	observations := make([]string, 0, 2)
	attempted := 0
	status := "unreachable"
	defer func() {
		report, _ := json.Marshal(struct {
			Kind         string `json:"kind"`
			V            int    `json:"v"`
			Status       string `json:"status"`
			Observations int    `json:"observations"`
			Attempted    int    `json:"attemptedEndpoints"`
		}{"linkgate.stun", 1, status, len(observations), attempted})
		fmt.Println(string(report))
	}()
	for _, endpoint := range endpoints {
		attempted++
		response, requestErr := connection.doSTUNRequest(endpoint.String())
		if requestErr != nil {
			continue
		}
		mapped, parseErr := linkgateSTUNEndpoint(response.externalAddr)
		if parseErr != nil {
			continue
		}
		observations = append(observations, mapped.String())
		// OTHER-ADDRESS is not a second observation or permission to contact
		// an unconfigured endpoint. The next configured request is independent.
		if len(observations) >= 2 {
			status = "sufficient"
			return observations, connection.localAddr, nil
		}
	}
	if len(observations) > 0 {
		status = "insufficient"
	}
	return nil, connection.localAddr, errors.New("STUN_OBSERVATIONS_INSUFFICIENT")
}
