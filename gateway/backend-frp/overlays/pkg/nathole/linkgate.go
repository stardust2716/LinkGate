package nathole

import (
	"github.com/fatedier/frp/pkg/msg"
	"github.com/fatedier/frp/pkg/transport"
	"github.com/stardust2716/Redesk/gateway/control-plane/authority"
)

// Enabled only during authenticated service construction, before any request.
func (c *Controller) RequireLinkGate() { c.linkgateRequired = true }
func (c *Controller) HandleAuthorizedVisitor(message *msg.NatHoleVisitor, transporter transport.MessageTransporter, visitorUser string, permit *authority.TransportAuthorization) {
	if permit == nil || !permit.Alive() || permit.VisitorNamespace() != visitorUser || permit.ProxyName() != message.ProxyName || permit.Kind() != "xtcp" {
		_ = transporter.Send(c.GenNatHoleResponse(message.TransactionID, nil, "AUTH_REQUIRED"))
		return
	}
	c.handleVisitor(message, transporter, visitorUser, permit)
}
func (c *Controller) HandleAuthorizedClient(message *msg.NatHoleClient, transporter transport.MessageTransporter, publisherUser string) {
	c.mu.RLock()
	session := c.sessions[message.Sid]
	valid := session != nil && session.linkgatePermit != nil && session.linkgatePermit.Alive() && session.linkgatePermit.PublisherNamespace() == publisherUser && session.visitorMsg.ProxyName == message.ProxyName
	c.mu.RUnlock()
	if !valid {
		return
	}
	c.handleClient(message, transporter, publisherUser)
}
func (c *Controller) HandleAuthorizedReport(message *msg.NatHoleReport, subject string) {
	c.mu.RLock()
	session := c.sessions[message.Sid]
	valid := session != nil && session.linkgatePermit != nil && session.linkgatePermit.Alive() && (session.linkgatePermit.VisitorNamespace() == subject || session.linkgatePermit.PublisherNamespace() == subject)
	c.mu.RUnlock()
	if !valid {
		return
	}
	c.handleReport(message, subject)
}
