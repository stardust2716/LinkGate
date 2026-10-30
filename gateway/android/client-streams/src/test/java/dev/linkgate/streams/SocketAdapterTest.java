package dev.linkgate.streams;

import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.Arrays;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.Test;
import static org.junit.Assert.*;

public final class SocketAdapterTest {
    private static final class Backing implements AutoCloseable {
        final Socket local,remote;
        Backing()throws Exception{try(ServerSocket listener=new ServerSocket()){listener.bind(new InetSocketAddress(InetAddress.getByAddress(new byte[]{127,0,0,1}),0));local=new Socket();local.connect(listener.getLocalSocketAddress(),1000);remote=listener.accept();local.setSoTimeout(2000);remote.setSoTimeout(2000);}}
        SocketAdapter.Duplex duplex(){return new SocketAdapter.Duplex(){public InputStream input(){try{return local.getInputStream();}catch(Exception failure){throw new RuntimeException(failure);}}public OutputStream output(){try{return local.getOutputStream();}catch(Exception failure){throw new RuntimeException(failure);}}public void finishWrites()throws java.io.IOException{local.shutdownOutput();}public void close()throws java.io.IOException{local.close();}};}
        @Override public void close()throws Exception{local.close();remote.close();}
    }
    private static void stop(ExecutorService workers)throws Exception{workers.shutdownNow();assertTrue(workers.awaitTermination(3,TimeUnit.SECONDS));}
    @Test public void requestFinPreservesEntireResponse()throws Exception {
        ExecutorService workers=Executors.newFixedThreadPool(3);
        try(Backing backing=new Backing();Socket application=SocketAdapter.connect(backing.duplex(),workers)) {
            byte[] response=new byte[1024*1024];for(int i=0;i<response.length;i++)response[i]=(byte)(i%251);
            var backend=workers.submit(()->{try{assertArrayEquals(new byte[]{1,2,3},backing.remote.getInputStream().readAllBytes());backing.remote.getOutputStream().write(response);backing.remote.shutdownOutput();}catch(Exception failure){throw new RuntimeException(failure);}});
            application.setSoTimeout(3000);application.getOutputStream().write(new byte[]{1,2,3});application.shutdownOutput();assertArrayEquals(response,application.getInputStream().readAllBytes());backend.get(3,TimeUnit.SECONDS);assertFalse(application.isClosed());
        }finally{stop(workers);}
    }
    @Test public void applicationCloseReleasesBlockedDescriptor()throws Exception {
        ExecutorService workers=Executors.newFixedThreadPool(2);
        try(Backing backing=new Backing()) {
            Socket application=SocketAdapter.connect(backing.duplex(),workers);application.close();assertTrue(backing.local.isClosed());assertEquals(-1,backing.remote.getInputStream().read());
        }finally{stop(workers);}
    }
    @Test public void secondWorkerRejectionClosesFirstWorkerAndBothSockets()throws Exception {
        ExecutorService workers=Executors.newSingleThreadExecutor();AtomicInteger calls=new AtomicInteger();
        try(Backing backing=new Backing()) {
            try{SocketAdapter.connect(backing.duplex(),task->{if(calls.incrementAndGet()==2)throw new RejectedExecutionException();workers.execute(task);});fail("Capacity refusal ignored.");}catch(java.io.IOException expected){assertTrue(backing.local.isClosed());assertEquals(-1,backing.remote.getInputStream().read());}
        }finally{stop(workers);}
    }
    @Test public void descriptorFailureInterruptsApplicationInsteadOfCleanFin()throws Exception {
        ExecutorService workers=Executors.newFixedThreadPool(2);var entered=new java.util.concurrent.CountDownLatch(1);var release=new java.util.concurrent.CountDownLatch(1);
        SocketAdapter.Duplex failing=new SocketAdapter.Duplex(){public InputStream input(){return new InputStream(){public int read()throws java.io.IOException{entered.countDown();try{release.await(2,TimeUnit.SECONDS);}catch(InterruptedException e){Thread.currentThread().interrupt();}throw new java.io.IOException("Fixed stream failure.");}};}public OutputStream output(){return OutputStream.nullOutputStream();}public void finishWrites(){}public void close(){release.countDown();}};
        try(Socket application=SocketAdapter.connect(failing,workers)){assertTrue(entered.await(2,TimeUnit.SECONDS));application.setSoTimeout(2000);release.countDown();try{application.getInputStream().read();fail("Interrupted stream became normal EOF.");}catch(java.net.SocketException expected){}}
        finally{release.countDown();stop(workers);}
    }
}
