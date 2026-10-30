package dev.linkgate.client;

import java.io.IOException;

public final class NativeRefusalException extends IOException {
    public final String code;
    NativeRefusalException(int value){super("Private descriptor request rejected.");switch(value){case 1:code="AUTH_REQUIRED";break;case 2:code="GATEWAY_STOPPED";break;case 3:code="RESOURCE_EXHAUSTED";break;case 4:code="INVALID_REQUEST";break;case 5:code="CAPABILITY_UNSUPPORTED";break;case 6:code="STORAGE_UNAVAILABLE";break;default:throw new IllegalArgumentException("Unknown native refusal.");}}
}
