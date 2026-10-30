package nathole

import (
	"encoding/json"
	"github.com/stretchr/testify/require"
	"net"
	"testing"
	"time"
)

func linkgateSTUNList(values ...string) string {
	result, _ := json.Marshal(values)
	return string(result)
}
func linkgateOneMapping(server *net.UDPConn, port int, other *net.UDPAddr) <-chan testSTUNExchange {
	return serveOneSTUNRequest(server, func(request []byte, _ *net.UDPAddr) ([]byte, error) {
		attrs := []testSTUNAttribute{{typ: testAttrXORMapped, value: testIPv4AddressValue(net.ParseIP("198.51.100.10"), port, true)}}
		if other != nil {
			attrs = append(attrs, testSTUNAttribute{typ: testAttrOther, value: testIPv4AddressValue(other.IP, other.Port, false)})
		}
		return makeTestSTUNResponse(request, testBindingSuccess, attrs...)
	})
}
func TestLinkGateAgentSTUNIndependentDestinationsShareSocket(t *testing.T) {
	for _, port := range []int{40000, 40001} {
		primary, second := listenTestUDP4(t), listenTestUDP4(t)
		a, b := linkgateOneMapping(primary, 40000, nil), linkgateOneMapping(second, port, nil)
		mapped, local, err := linkgateDiscover(linkgateSTUNList(primary.LocalAddr().String(), second.LocalAddr().String()), "")
		require.NoError(t, err)
		require.Len(t, mapped, 2)
		first, last := waitSTUNExchange(t, a), waitSTUNExchange(t, b)
		require.Equal(t, first.Port, last.Port)
		require.Equal(t, first.Port, local.(*net.UDPAddr).Port)
		feature, err := ClassifyNATFeature(mapped, nil)
		require.NoError(t, err)
		if port == 40000 {
			require.Equal(t, EasyNAT, feature.NatType)
		} else {
			require.Equal(t, HardNAT, feature.NatType)
		}
	}
}
func TestLinkGateAgentSTUNRetainsObservationAcrossFailedEndpoint(t *testing.T) {
	first, failed, last := listenTestUDP4(t), listenTestUDP4(t), listenTestUDP4(t)
	a, c := linkgateOneMapping(first, 40000, nil), linkgateOneMapping(last, 40000, nil)
	b := serveOneSTUNRequest(failed, func(request []byte, _ *net.UDPAddr) ([]byte, error) {
		return makeTestSTUNResponse(request, testBindingError, testSTUNAttribute{typ: testAttrErrorCode, value: []byte{0, 0, 4, 0}})
	})
	mapped, _, err := linkgateDiscover(linkgateSTUNList(first.LocalAddr().String(), failed.LocalAddr().String(), last.LocalAddr().String()), "")
	require.NoError(t, err)
	require.Len(t, mapped, 2)
	waitSTUNExchange(t, a)
	waitSTUNExchange(t, b)
	waitSTUNExchange(t, c)
}
func TestLinkGateAgentSTUNNeverFollowsUnconfiguredOtherAddress(t *testing.T) {
	primary, other := listenTestUDP4(t), listenTestUDP4(t)
	a := linkgateOneMapping(primary, 40000, other.LocalAddr().(*net.UDPAddr))
	mapped, _, err := linkgateDiscover(linkgateSTUNList(primary.LocalAddr().String()), "")
	require.Error(t, err)
	require.Empty(t, mapped)
	waitSTUNExchange(t, a)
	require.NoError(t, other.SetReadDeadline(time.Now().Add(30*time.Millisecond)))
	_, _, err = other.ReadFromUDP(make([]byte, 64))
	require.Error(t, err)
}
func TestLinkGateAgentSTUNRejectsConfigurationBeforeSending(t *testing.T) {
	primary := listenTestUDP4(t)
	endpoint := primary.LocalAddr().String()
	for _, raw := range []string{"", "null", "[]", "{}", "[1]", "[\"198.18.0.1:3478\"]", "[\"198.19.0.1:3478\"]", "[\"[::1]:3478\"]", "[\"0.0.0.0:3478\"]", "[\"224.0.0.1:3478\"]", "[\"255.255.255.255:3478\"]", "[\"169.254.1.1:3478\"]", "[\"127.0.0.1:0\"]", "[\"stun.example.invalid:3478\"]", linkgateSTUNList(endpoint, endpoint), linkgateSTUNList(endpoint, "127.0.0.1:1", "127.0.0.1:2", "127.0.0.1:3", "127.0.0.1:4")} {
		_, local, err := linkgateDiscover(raw, "")
		require.Error(t, err)
		require.Nil(t, local)
	}
	require.NoError(t, primary.SetReadDeadline(time.Now().Add(30*time.Millisecond)))
	_, _, err := primary.ReadFromUDP(make([]byte, 64))
	require.Error(t, err)
}
func TestLinkGateAgentSTUNRejectsInvalidMapping(t *testing.T) {
	primary, second := listenTestUDP4(t), listenTestUDP4(t)
	a := serveOneSTUNRequest(primary, func(request []byte, _ *net.UDPAddr) ([]byte, error) {
		return makeTestSTUNResponse(request, testBindingSuccess, testSTUNAttribute{typ: testAttrXORMapped, value: testIPv4AddressValue(net.ParseIP("198.18.0.1"), 40000, true)})
	})
	b := linkgateOneMapping(second, 40000, nil)
	mapped, _, err := linkgateDiscover(linkgateSTUNList(primary.LocalAddr().String(), second.LocalAddr().String()), "")
	require.Error(t, err)
	require.Empty(t, mapped)
	waitSTUNExchange(t, a)
	waitSTUNExchange(t, b)
}
func TestLinkGateAgentSTUNEnvironmentCannotDowngradeToLegacy(t *testing.T) {
	t.Setenv("LINKGATE_AGENT_PIPE", "private-parent")
	t.Setenv("LINKGATE_STUN_ENDPOINTS", "")
	_, local, err := Discover([]string{"127.0.0.1:3478"}, "")
	require.Error(t, err)
	require.Nil(t, local)
}
