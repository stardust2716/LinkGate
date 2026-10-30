package dev.linkgate.client;

import java.io.IOException;
import java.util.UUID;

public final class MutationOutcomeUnknownException extends IOException {
    private static final long serialVersionUID=1L;
    public final UUID operationId;
    MutationOutcomeUnknownException(UUID id){super("Operation outcome unknown; explicitly query the original operation ID.");operationId=id;}
}
