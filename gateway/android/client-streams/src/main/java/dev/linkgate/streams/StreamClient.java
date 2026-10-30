package dev.linkgate.streams;

import android.os.Binder;
import dev.linkgate.client.BrokerClient;
import java.io.IOException;
import java.net.Socket;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

public final class StreamClient {
    private StreamClient() { }
    private static final ThreadFactory THREADS=work->{Thread thread=new Thread(work,"LinkGate-stream");thread.setDaemon(true);return thread;};
    private static final ThreadPoolExecutor SETUP=new ThreadPoolExecutor(0,2,30,TimeUnit.SECONDS,new SynchronousQueue<>(),THREADS);
    private static final ThreadPoolExecutor COPY=new ThreadPoolExecutor(0,32,30,TimeUnit.SECONDS,new SynchronousQueue<>(),THREADS);
    private static final ScheduledThreadPoolExecutor TIMER=new ScheduledThreadPoolExecutor(1,THREADS);
    static{TIMER.setRemoveOnCancelPolicy(true);}
    public static CompletableFuture<Socket> attachSocketAsync(BrokerClient client,UUID operation,boolean allowExperimental) {
        return attachSocketAsync(client,operation,operation,allowExperimental,false);
    }
    public static CompletableFuture<Socket> attachConnectionSocketAsync(BrokerClient client,UUID streamRef,UUID attachOperation,boolean allowExperimental){return attachSocketAsync(client,streamRef,attachOperation,allowExperimental,true);}
    private static CompletableFuture<Socket> attachSocketAsync(BrokerClient client,UUID reference,UUID operation,boolean allowExperimental,boolean formal) {
        if(client==null||operation==null)throw new IllegalArgumentException("Verified client and original operation required.");
        CompletableFuture<Socket> result=new CompletableFuture<>();AtomicReference<DescriptorDuplex> held=new AtomicReference<>();
        Binder lifetime=new Binder();var pending=formal?client.attachConnectionDescriptorAsync(reference,operation,lifetime,allowExperimental):client.attachStreamAsync(reference,lifetime,allowExperimental);
        var timeout=TIMER.schedule(()->{DescriptorDuplex value=held.get();if(value!=null)try{value.close();}catch(IOException ignored){}result.completeExceptionally(new IOException("Descriptor setup timed out."));pending.cancel(false);},2,TimeUnit.SECONDS);
        result.whenComplete((value,error)->{timeout.cancel(false);if(error!=null){pending.cancel(false);DescriptorDuplex stream=held.get();if(stream!=null)try{stream.close();}catch(IOException ignored){}}});
        pending.whenComplete((descriptor,error)->{
            if(error!=null){result.completeExceptionally(error);return;}
            final DescriptorDuplex stream;
            try{stream=new DescriptorDuplex(descriptor,lifetime);held.set(stream);}catch(IOException e){result.completeExceptionally(new IOException("Descriptor unavailable."));return;}
            if(result.isDone()){try{stream.close();}catch(IOException ignored){}return;}
            try{SETUP.execute(()->{
                try{stream.attach();Socket socket=SocketAdapter.connect(stream,COPY);if(!result.complete(socket))socket.close();}
                catch(Exception e){try{stream.close();}catch(IOException ignored){}result.completeExceptionally(new IOException("Private stream setup failed."));}
            });}catch(RuntimeException e){try{stream.close();}catch(IOException ignored){}result.completeExceptionally(new IOException("RESOURCE_EXHAUSTED"));}
        });return result;
    }
}
