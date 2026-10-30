package dev.linkgate.transport;

import dev.linkgate.contracts.ConnectionPolicy;
import java.io.Closeable;
import java.io.IOException;
import java.security.GeneralSecurityException;
import java.util.ArrayList;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.function.BooleanSupplier;

/** Agent-only planning: attempts return authenticated transports, never application payloads. */
public final class ConnectionPlanner<V extends Closeable> {
    public enum Code {AUTH_REQUIRED,RESOURCE_EXHAUSTED,CAPABILITY_UNSUPPORTED,CLOCK_UNTRUSTED,CANCELLED,CONNECT_TIMEOUT,DIRECT_WINDOW_EXPIRED,DIRECT_FAILED,RELAY_FAILED}
    public enum Fallback {NONE,DIRECT_FAILED,DIRECT_WINDOW_EXPIRED}
    public static final class Failure extends IOException {
        public final Code code;
        public Failure(Code code){super("Connection planning failed: "+code.name());this.code=code;}
    }
    public interface Clock {long nanos();}
    public interface Attempt<V> {V open(Context context)throws Exception;}
    /** Register raw abort handles before TLS wrappers or other potentially blocked cleanup. */
    public static final class Context implements Closeable {
        private final Watch watch;private final long deadline;private final ArrayList<AutoCloseable> resources=new ArrayList<>();private volatile boolean cancelled;
        private Context(Watch watch,long deadline){this.watch=watch;this.deadline=deadline;}
        public boolean cancelled(){return cancelled;}
        public long deadlineNanos(){return deadline;}
        public int remainingMillis()throws Failure {
            if(cancelled)throw new Failure(Code.CANCELLED);long left=deadline-watch.read();if(left<=0)throw new Failure(Code.CONNECT_TIMEOUT);return (int)Math.min(60000,Math.max(1,TimeUnit.NANOSECONDS.toMillis(left)));
        }
        public void track(AutoCloseable resource)throws Failure {
            if(resource==null)throw new IllegalArgumentException("Owned resource required.");boolean close=false,exhausted=false;
            synchronized(this){for(AutoCloseable prior:resources)if(prior==resource)return;if(cancelled)close=true;else if(resources.size()>=16){close=true;exhausted=true;}else resources.add(resource);}
            if(close){quiet(resource);if(exhausted)close();throw new Failure(exhausted?Code.RESOURCE_EXHAUSTED:Code.CANCELLED);}
        }
        @Override public void close(){ArrayList<AutoCloseable> closing;synchronized(this){if(cancelled)return;cancelled=true;closing=new ArrayList<>(resources);resources.clear();}for(AutoCloseable resource:closing)quiet(resource);}
    }
    public final class Result implements Closeable {
        public final String path;public final Fallback fallback;public final long setupMs;
        private final Candidate candidate;private final V value;
        private Result(Candidate candidate,V value,String path,Fallback fallback,long setupMs){this.candidate=candidate;this.value=value;this.path=path;this.fallback=fallback;this.setupMs=setupMs;}
        public V value()throws Failure {if(candidate.context.cancelled())throw new Failure(Code.CANCELLED);return value;}
        @Override public void close(){candidate.context.close();}
    }
    private static final class Watch {
        final Clock clock;long last;boolean invalid;
        Watch(Clock clock,long admitted)throws Failure{this.clock=clock;last=admitted;read();}
        synchronized long read()throws Failure {long now=clock.nanos();if(invalid||now<last){invalid=true;throw new Failure(Code.CLOCK_UNTRUSTED);}last=now;return now;}
    }
    private final class Ready {
        final V value;final long time;
        Ready(V value,long time){this.value=value;this.time=time;}
    }
    private final class Candidate {
        final Context context;final CompletableFuture<Ready> ready=new CompletableFuture<>();final Code failed;
        Candidate(Watch watch,long deadline,Attempt<V> attempt,Executor executor,Code failed){
            context=new Context(watch,deadline);this.failed=failed;
            try{executor.execute(()->{
                try{if(context.cancelled())throw new Failure(Code.CANCELLED);V value=attempt.open(context);if(value==null)throw new Failure(failed);context.track(value);long time=watch.read();if(time>=deadline)throw new Failure(Code.CONNECT_TIMEOUT);if(context.cancelled())throw new Failure(Code.CANCELLED);ready.complete(new Ready(value,time));}
                catch(Exception failure){context.close();ready.completeExceptionally(classify(failure,failed));}
            });}catch(RuntimeException exhausted){context.close();ready.completeExceptionally(new Failure(Code.RESOURCE_EXHAUSTED));}
        }
    }
    private final Clock clock;private final Executor directIo,relayIo;
    public ConnectionPlanner(Clock clock,Executor directIo,Executor relayIo){if(clock==null||directIo==null||relayIo==null)throw new IllegalArgumentException("Host bounded executors and monotonic clock required.");this.clock=clock;this.directIo=directIo;this.relayIo=relayIo;}
    public Result connect(ConnectionPolicy policy,long admitted,BooleanSupplier cancelled,Attempt<V> direct,Attempt<V> relay)throws Failure {
        if(policy==null||cancelled==null||direct==null||relay==null)throw new IllegalArgumentException("Effective approved policy and owned attempts required.");
        if(policy.prewarm||policy.relayBudgetBytes!=null||policy.relayWarningBytes!=null)throw new Failure(Code.CAPABILITY_UNSUPPORTED);
        Watch watch=new Watch(clock,admitted);long end,directEnd;
        try{end=Math.addExact(admitted,TimeUnit.MILLISECONDS.toNanos(policy.connectTimeoutMs));directEnd=policy.allowRelay?Math.min(end,Math.addExact(admitted,TimeUnit.MILLISECONDS.toNanos(policy.directWaitMs))):end;}catch(ArithmeticException invalid){throw new Failure(Code.CLOCK_UNTRUSTED);}
        Candidate first=null,second=null,selected=null;Fallback fallback=Fallback.NONE;
        try {
            if(cancelled.getAsBoolean())throw new Failure(Code.CANCELLED);if(watch.read()>=end)throw new Failure(Code.CONNECT_TIMEOUT);
            if(watch.read()<directEnd){
                first=new Candidate(watch,directEnd,direct,directIo,Code.DIRECT_FAILED);
                try{Ready value=await(first,watch,directEnd,end,cancelled,true);selected=first;return new Result(first,value.value,"direct",Fallback.NONE,TimeUnit.NANOSECONDS.toMillis(value.time-admitted));}
                catch(Failure failure){if(terminal(failure.code))throw failure;fallback=failure.code==Code.DIRECT_WINDOW_EXPIRED||failure.code==Code.CONNECT_TIMEOUT?Fallback.DIRECT_WINDOW_EXPIRED:Fallback.DIRECT_FAILED;first.context.close();}
            }else fallback=Fallback.DIRECT_WINDOW_EXPIRED;
            if(!policy.allowRelay)throw new Failure(watch.read()>=end?Code.CONNECT_TIMEOUT:Code.DIRECT_FAILED);
            if(cancelled.getAsBoolean())throw new Failure(Code.CANCELLED);if(watch.read()>=end)throw new Failure(Code.CONNECT_TIMEOUT);
            second=new Candidate(watch,end,relay,relayIo,Code.RELAY_FAILED);Ready value=await(second,watch,end,end,cancelled,false);selected=second;return new Result(second,value.value,"relay",fallback,TimeUnit.NANOSECONDS.toMillis(value.time-admitted));
        }finally{if(first!=null&&first!=selected)first.context.close();if(second!=null&&second!=selected)second.context.close();}
    }
    private Ready await(Candidate candidate,Watch watch,long phaseEnd,long totalEnd,BooleanSupplier cancelled,boolean direct)throws Failure {
        while(true){
            if(cancelled.getAsBoolean())throw new Failure(Code.CANCELLED);long now=watch.read();if(now>=totalEnd)throw new Failure(Code.CONNECT_TIMEOUT);
            try {
                Ready value;
                if(candidate.ready.isDone())value=candidate.ready.get();
                else{if(now>=phaseEnd)throw new Failure(direct?Code.DIRECT_WINDOW_EXPIRED:Code.CONNECT_TIMEOUT);value=candidate.ready.get(Math.min(phaseEnd-now,TimeUnit.MILLISECONDS.toNanos(25)),TimeUnit.NANOSECONDS);}
                if(cancelled.getAsBoolean()||candidate.context.cancelled())throw new Failure(Code.CANCELLED);
                if(watch.read()>=totalEnd)throw new Failure(Code.CONNECT_TIMEOUT);
                if(value.time>=phaseEnd)throw new Failure(direct?Code.DIRECT_WINDOW_EXPIRED:Code.CONNECT_TIMEOUT);return value;
            }catch(TimeoutException pending){}
            catch(InterruptedException interrupted){Thread.currentThread().interrupt();throw new Failure(Code.CANCELLED);}
            catch(ExecutionException failure){Throwable reason=failure.getCause();throw reason instanceof Failure?(Failure)reason:new Failure(candidate.failed);}
        }
    }
    private static boolean terminal(Code code){return code==Code.AUTH_REQUIRED||code==Code.RESOURCE_EXHAUSTED||code==Code.CAPABILITY_UNSUPPORTED||code==Code.CLOCK_UNTRUSTED||code==Code.CANCELLED;}
    private static Failure classify(Exception failure,Code transport){if(failure instanceof Failure)return (Failure)failure;if(failure instanceof GeneralSecurityException||failure instanceof SecurityException)return new Failure(Code.AUTH_REQUIRED);if(failure instanceof InterruptedException){Thread.currentThread().interrupt();return new Failure(Code.CANCELLED);}return new Failure(transport);}
    private static void quiet(AutoCloseable value){try{value.close();}catch(Exception ignored){}}
}
