package nathole

import (
	"github.com/stretchr/testify/require"
	"net"
	"strconv"
	"testing"
	"time"
)

func oneMapped(server *net.UDPConn, port int, other *net.UDPAddr) <-chan testSTUNExchange {
	return serveOneSTUNRequest(server, func(request []byte, _ *net.UDPAddr) ([]byte, error) {
		attrs := []testSTUNAttribute{{typ: testAttrXORMapped, value: testIPv4AddressValue(net.ParseIP("198.51.100.10"), port, true)}}
		if other != nil {
			attrs = append(attrs, testSTUNAttribute{typ: testAttrOther, value: testIPv4AddressValue(other.IP, other.Port, false)})
		}
		return makeTestSTUNResponse(request, testBindingSuccess, attrs...)
	})
}

func TestLinkgateIndependentServersReuseSocketAndPreserveNATType(t *testing.T) {
	for _, port := range []int{40000, 40001} {
		t.Run("mapping-"+strconv.Itoa(port), func(t *testing.T) {
			primary, backup := listenTestUDP4(t), listenTestUDP4(t)
			a, b := oneMapped(primary, 40000, nil), oneMapped(backup, port, nil)
			addresses, local, e := discoverWithFallback([]string{primary.LocalAddr().String()}, []string{backup.LocalAddr().String()}, "")
			require.NoError(t, e)
			require.Len(t, addresses, 2)
			first, second := waitSTUNExchange(t, a), waitSTUNExchange(t, b)
			require.Equal(t, first.Port, second.Port)
			require.Equal(t, first.Port, local.(*net.UDPAddr).Port)
			feature, e := ClassifyNATFeature(addresses, nil)
			require.NoError(t, e)
			if port == 40000 {
				require.Equal(t, EasyNAT, feature.NatType)
			} else {
				require.Equal(t, HardNAT, feature.NatType)
			}
		})
	}
}

func TestLinkgatePrimaryFailureCanUseTwoBackups(t *testing.T) {
	primary, a, b := listenTestUDP4(t), listenTestUDP4(t), listenTestUDP4(t)
	failed := serveOneSTUNRequest(primary, func(request []byte, _ *net.UDPAddr) ([]byte, error) {
		return makeTestSTUNResponse(request, testBindingError, testSTUNAttribute{typ: testAttrErrorCode, value: []byte{0, 0, 4, 0}})
	})
	da, db := oneMapped(a, 40000, nil), oneMapped(b, 40000, nil)
	addresses, _, e := discoverWithFallback([]string{primary.LocalAddr().String()}, []string{a.LocalAddr().String(), b.LocalAddr().String()}, "")
	require.NoError(t, e)
	require.Len(t, addresses, 2)
	p, pa, pb := waitSTUNExchange(t, failed), waitSTUNExchange(t, da), waitSTUNExchange(t, db)
	require.Equal(t, p.Port, pa.Port)
	require.Equal(t, pa.Port, pb.Port)
}

func TestLinkgateDuplicateDestinationDoesNotFabricateSecondObservation(t *testing.T) {
	primary := listenTestUDP4(t)
	done := oneMapped(primary, 40000, primary.LocalAddr().(*net.UDPAddr))
	addresses, _, e := discoverWithFallback([]string{primary.LocalAddr().String()}, []string{primary.LocalAddr().String()}, "")
	require.Error(t, e)
	require.Empty(t, addresses)
	waitSTUNExchange(t, done)
}

func TestLinkgateOtherAddressCompletesWithoutBackup(t *testing.T) {
	primary, other, backup := listenTestUDP4(t), listenTestUDP4(t), listenTestUDP4(t)
	a, b := oneMapped(primary, 40000, other.LocalAddr().(*net.UDPAddr)), oneMapped(other, 40000, nil)
	addresses, _, e := discoverWithFallback([]string{primary.LocalAddr().String()}, []string{backup.LocalAddr().String()}, "")
	require.NoError(t, e)
	require.Len(t, addresses, 2)
	waitSTUNExchange(t, a)
	waitSTUNExchange(t, b)
	require.NoError(t, backup.SetReadDeadline(time.Now().Add(30*time.Millisecond)))
	_, _, e = backup.ReadFromUDP(make([]byte, 64))
	require.Error(t, e)
}

func TestLinkgateBrokenOtherRetainsPrimaryObservation(t *testing.T) {
	primary, other, backup := listenTestUDP4(t), listenTestUDP4(t), listenTestUDP4(t)
	a := oneMapped(primary, 40000, other.LocalAddr().(*net.UDPAddr))
	bad := serveOneSTUNRequest(other, func(request []byte, _ *net.UDPAddr) ([]byte, error) {
		return makeTestSTUNResponse(request, testBindingError, testSTUNAttribute{typ: testAttrErrorCode, value: []byte{0, 0, 4, 0}})
	})
	b := oneMapped(backup, 40000, nil)
	addresses, _, e := discoverWithFallback([]string{primary.LocalAddr().String()}, []string{backup.LocalAddr().String()}, "")
	require.NoError(t, e)
	require.Len(t, addresses, 2)
	waitSTUNExchange(t, a)
	waitSTUNExchange(t, bad)
	waitSTUNExchange(t, b)
}

func TestLinkgateRejectsFakeIPAndInvalidEndpoint(t *testing.T) {
	for _, address := range []string{"198.18.0.1:3478", "198.19.1.1:3478", "127.0.0.1:0", "127.0.0.1:65536", "[::1]:3478"} {
		_, e := resolveLinkgateSTUN(address)
		require.Error(t, e)
	}
}
