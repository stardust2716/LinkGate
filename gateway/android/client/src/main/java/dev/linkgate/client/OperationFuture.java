package dev.linkgate.client;

import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicInteger;

public final class OperationFuture<T> extends CompletableFuture<T> {
    public final UUID operationId;
    // 0=available, 1=transport entry claimed, 2=cancelled before entry.
    private final AtomicInteger submission=new AtomicInteger();
    OperationFuture(UUID operationId){if(operationId==null)throw new IllegalArgumentException("Operation ID required.");this.operationId=operationId;}
    boolean tryMarkAttempted(){return submission.compareAndSet(0,1);}
    @Override public boolean cancel(boolean mayInterruptIfRunning){
        submission.compareAndSet(0,2);
        return super.cancel(mayInterruptIfRunning);
    }
    public boolean mayHaveBeenSubmitted(){return submission.get()==1;}
}
