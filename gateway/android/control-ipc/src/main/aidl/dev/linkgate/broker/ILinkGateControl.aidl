package dev.linkgate.broker;

import dev.linkgate.broker.StreamAttachment;

interface ILinkGateControl {
    byte[] request(in byte[] message);
    StreamAttachment attachStream(String operationId, IBinder lifetimeToken);
    StreamAttachment attachConnection(String streamRef, IBinder lifetimeToken);
}
