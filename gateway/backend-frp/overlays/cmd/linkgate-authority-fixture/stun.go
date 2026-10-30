package main

import (
	"encoding/binary"
	"net"
	"sync"
	"sync/atomic"
	"time"

	"github.com/fatedier/golib/net/stun"
)

// This test-only responder deliberately has no public listen option. It is not
// a NAT classification service: both observation endpoints use loopback.
type loopbackSTUN struct {
	sockets      [2]*net.UDPConn
	workers      sync.WaitGroup
	stop         sync.Once
	observations [2]atomic.Uint64
}

func newLoopbackSTUN() (*loopbackSTUN, error) {
	s := new(loopbackSTUN)
	for i := range s.sockets {
		connection, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
		if err != nil {
			s.Close()
			return nil, err
		}
		s.sockets[i] = connection
	}
	for i := range s.sockets {
		s.workers.Add(1)
		go s.serve(i)
	}
	return s, nil
}
func (s *loopbackSTUN) Port() int       { return s.sockets[0].LocalAddr().(*net.UDPAddr).Port }
func (s *loopbackSTUN) SecondPort() int { return s.sockets[1].LocalAddr().(*net.UDPAddr).Port }
func (s *loopbackSTUN) Observations() [2]uint64 {
	return [2]uint64{s.observations[0].Load(), s.observations[1].Load()}
}
func (s *loopbackSTUN) Close() {
	s.stop.Do(func() {
		for _, connection := range s.sockets {
			if connection != nil {
				_ = connection.Close()
			}
		}
	})
	s.workers.Wait()
}
func (s *loopbackSTUN) serve(index int) {
	defer s.workers.Done()
	connection := s.sockets[index]
	alternate := s.sockets[1-index].LocalAddr().(*net.UDPAddr)
	var buffer [1024]byte
	for requests := 0; requests < 256; requests++ {
		_ = connection.SetReadDeadline(time.Now().Add(180 * time.Second))
		size, source, err := connection.ReadFromUDP(buffer[:])
		if err != nil {
			return
		}
		if !source.IP.IsLoopback() || size > 512 {
			continue
		}
		var message stun.Message
		if message.UnmarshalBinary(buffer[:size]) != nil || !message.IsBindingRequest() {
			continue
		}
		response := make([]byte, 44)
		binary.BigEndian.PutUint16(response, 0x0101)
		binary.BigEndian.PutUint16(response[2:], 24)
		binary.BigEndian.PutUint32(response[4:], 0x2112a442)
		transaction := message.TransactionID()
		copy(response[8:20], transaction[:])
		for i, endpoint := range []*net.UDPAddr{source, alternate} {
			offset := 20 + 12*i
			kind := uint16(0x0001)
			if i == 1 {
				kind = 0x802c
			}
			binary.BigEndian.PutUint16(response[offset:], kind)
			binary.BigEndian.PutUint16(response[offset+2:], 8)
			response[offset+5] = 1
			binary.BigEndian.PutUint16(response[offset+6:], uint16(endpoint.Port))
			copy(response[offset+8:offset+12], endpoint.IP.To4())
		}
		if _, err = connection.WriteToUDP(response, source); err == nil {
			s.observations[index].Add(1)
		}
	}
}
