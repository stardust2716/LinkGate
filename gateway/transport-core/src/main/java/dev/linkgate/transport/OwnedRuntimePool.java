package dev.linkgate.transport;

import java.io.Closeable;
import java.io.IOException;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.function.BooleanSupplier;
import java.util.function.Predicate;

/** Agent-only scoped ownership; callers supply bounded creation and maintenance executors. */
public final class OwnedRuntimePool<K,V extends Closeable> implements Closeable {
    public static final class Exhausted extends IOException {public Exhausted(){super("RESOURCE_EXHAUSTED");}}
    public interface Clock {long nanos();}
    public interface Creation<V> extends BooleanSupplier {void own(V resource)throws IOException;}
    public interface Factory<V> {V create(Creation<V> creation)throws Exception;}
    private final Clock clock;private final Executor creation;private final long idleNanos;private final Semaphore slots;
    private final Map<K,Record> records=new HashMap<>();private final Set<Record> owned=new HashSet<>();
    private boolean closed;private long lastNanos;
    private final class Record {
        final K key;final CompletableFuture<V> ready=new CompletableFuture<>();int references=1;
        volatile boolean retired;boolean creating=true,closing,disposed;long idleSince;V value,unexpected;IOException failure;
        Record(K key){this.key=key;}
    }
    public final class Lease implements Closeable {
        private final Record record;private boolean released;
        private Lease(Record record){this.record=record;}
        public V value()throws IOException{synchronized(OwnedRuntimePool.this){if(released||record.retired||record.creating||record.value==null)throw unavailable();return record.value;}}
        @Override public void close(){
            boolean retire=false;
            synchronized(OwnedRuntimePool.this){if(released)return;released=true;if(--record.references==0&&!record.retired){if(record.creating)retire=true;else try{record.idleSince=now();}catch(IOException failure){retire=true;}}}
            if(retire)retire(record);
        }
    }
    public OwnedRuntimePool(int capacity,long idleMs,Clock clock,Executor creation) {
        if(capacity<1||capacity>1024||idleMs<1||idleMs>60000||clock==null||creation==null)throw new IllegalArgumentException("Bounded runtime pool required.");
        slots=new Semaphore(capacity);idleNanos=TimeUnit.MILLISECONDS.toNanos(idleMs);this.clock=clock;this.creation=creation;lastNanos=clock.nanos();
    }
    private synchronized long now()throws IOException {long current=clock.nanos();if(current<lastNanos)throw unavailable();lastNanos=current;return current;}
    private long remaining(long deadline)throws IOException {try{return Math.subtractExact(deadline,now());}catch(ArithmeticException failure){throw unavailable();}}
    public Lease acquire(K key,Factory<V> factory,long deadline,BooleanSupplier cancelled)throws Exception {
        if(key==null||factory==null||cancelled==null)throw new IllegalArgumentException("Owned scope and cancellation required.");
        if(cancelled.getAsBoolean()||remaining(deadline)<=0)throw unavailable();
        Record record;boolean create=false;
        synchronized(this){
            if(closed)throw unavailable();record=records.get(key);
            if(record==null){if(!slots.tryAcquire())throw new Exhausted();record=new Record(key);records.put(key,record);owned.add(record);create=true;}else record.references++;
        }
        Lease lease=new Lease(record);boolean delivered=false;
        try {
            if(create){Record selected=record;try{creation.execute(()->create(selected,factory));}catch(RuntimeException failure){synchronized(this){selected.creating=false;selected.failure=new Exhausted();}retire(selected);}}
            while(true){
                if(cancelled.getAsBoolean())throw unavailable();long left=remaining(deadline);if(left<=0)throw unavailable();
                try{record.ready.get(Math.min(left,TimeUnit.MILLISECONDS.toNanos(25)),TimeUnit.NANOSECONDS);break;}
                catch(TimeoutException pending){}
                catch(ExecutionException failure){if(failure.getCause() instanceof Exhausted)throw (Exhausted)failure.getCause();throw unavailable();}
            }
            if(cancelled.getAsBoolean()||remaining(deadline)<=0)throw unavailable();lease.value();delivered=true;return lease;
        }finally{if(!delivered)lease.close();}
    }
    private void create(Record record,Factory<V> factory) {
        V value=null;boolean valid=false;
        try{value=factory.create(new Creation<V>(){
            public boolean getAsBoolean(){return record.retired;}
            public void own(V resource)throws IOException{if(resource==null)throw unavailable();synchronized(OwnedRuntimePool.this){if(!record.creating||record.value!=null)throw unavailable();record.value=resource;}if(record.retired)throw unavailable();}
        });valid=value!=null;}
        catch(Exception failure){if(failure instanceof InterruptedException)Thread.currentThread().interrupt();}
        synchronized(this){
            record.creating=false;if(record.value==null)record.value=value;else if(value!=null&&record.value!=value){valid=false;record.unexpected=value;}
            if(valid&&!closed&&!record.retired&&record.references>0){record.ready.complete(value);return;}
        }
        retire(record);
    }
    private void retire(Record record) {
        synchronized(this){record.retired=true;records.remove(record.key,record);record.ready.completeExceptionally(record.failure==null?unavailable():record.failure);}
        dispose(record);
    }
    private void dispose(Record record) {
        V value,unexpected;
        synchronized(this){if(record.creating||record.closing||record.disposed)return;record.closing=true;value=record.value;unexpected=record.unexpected;}
        boolean success=true;
        try{if(value!=null)value.close();}catch(Exception failure){success=false;}
        try{if(unexpected!=null)unexpected.close();}catch(Exception failure){success=false;}
        synchronized(this){record.closing=false;if(success){record.disposed=true;owned.remove(record);slots.release();}}
    }
    public void maintain(Predicate<K> current,Predicate<V> alive) {
        if(current==null||alive==null)throw new IllegalArgumentException("Current authority and runtime checks required.");
        ArrayList<Record> snapshot;long time;
        try{time=now();}catch(IOException failure){close();return;}
        synchronized(this){snapshot=new ArrayList<>(owned);}
        for(Record record:snapshot){
            if(record.retired){dispose(record);continue;}
            boolean creating;V value;synchronized(this){creating=record.creating;value=record.value;}
            boolean valid;try{valid=current.test(record.key)&&(creating||alive.test(value));}catch(RuntimeException failure){valid=false;}
            boolean expired;synchronized(this){expired=!record.creating&&record.references==0&&time-record.idleSince>=idleNanos;}
            if(!valid||expired)retire(record);
        }
    }
    public synchronized int size(){return owned.size();}
    public synchronized int leaseCount(){int count=0;for(Record record:owned)count+=record.references;return count;}
    @Override public void close(){ArrayList<Record> snapshot;synchronized(this){closed=true;snapshot=new ArrayList<>(owned);}for(Record record:snapshot)retire(record);}
    private static IOException unavailable(){return new IOException("Owned runtime unavailable.");}
}
