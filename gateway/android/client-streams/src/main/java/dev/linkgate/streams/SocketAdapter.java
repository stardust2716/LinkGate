package dev.linkgate.streams;

import java.io.Closeable;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.concurrent.Executor;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

public final class SocketAdapter {
    private SocketAdapter() { }
    public interface Duplex extends Closeable { InputStream input();OutputStream output();void finishWrites()throws IOException; }
    public static Socket connect(Duplex duplex,Executor copyWorkers)throws IOException {
        OwnedSocket client=new OwnedSocket();Socket accepted=null;
        try(ServerSocket listener=new ServerSocket()) {
            InetAddress loopback=InetAddress.getByAddress(new byte[]{127,0,0,1});listener.setReuseAddress(false);listener.bind(new InetSocketAddress(loopback,0),2);listener.setSoTimeout(2000);
            client.setTcpNoDelay(true);client.bind(new InetSocketAddress(loopback,0));client.connect(listener.getLocalSocketAddress(),2000);
            long deadline=System.nanoTime()+2000000000L;
            for(int attempts=0;attempts<4;attempts++) {
                long remaining=deadline-System.nanoTime();if(remaining<=0)throw new IOException("Private socket setup deadline elapsed.");listener.setSoTimeout((int)Math.max(1,remaining/1000000));
                Socket candidate=listener.accept();
                if(candidate.getRemoteSocketAddress().equals(client.getLocalSocketAddress())&&candidate.getLocalSocketAddress().equals(client.getRemoteSocketAddress())){accepted=candidate;break;}
                candidate.close();
            }
            if(accepted==null)throw new IOException("Private socket ownership mismatch.");accepted.setTcpNoDelay(true);
            Bridge bridge=new Bridge(client,accepted,duplex);
            client.onClose.set(bridge::abort);
            try{copyWorkers.execute(bridge::outbound);copyWorkers.execute(bridge::inbound);}catch(RuntimeException e){bridge.abort();throw new IOException("Stream worker capacity exhausted.");}
            return client;
        }catch(IOException|RuntimeException e){quiet(accepted);quiet(client);quiet(duplex);throw e;}
    }
    private static final class OwnedSocket extends Socket {
        final java.util.concurrent.atomic.AtomicReference<Runnable> onClose=new java.util.concurrent.atomic.AtomicReference<>();
        @Override public void close()throws IOException{Runnable action=onClose.getAndSet(null);try{if(action!=null)action.run();}finally{super.close();}}
    }
    private static final class Bridge {
        private final Socket client,peer;private final Duplex stream;private final AtomicBoolean closed=new AtomicBoolean();private final AtomicInteger finished=new AtomicInteger();
        Bridge(Socket client,Socket peer,Duplex stream){this.client=client;this.peer=peer;this.stream=stream;}
        void outbound(){try{pump(peer.getInputStream(),stream.output());stream.finishWrites();done();}catch(Exception e){abort();}}
        void inbound(){try{pump(stream.input(),peer.getOutputStream());peer.shutdownOutput();done();}catch(Exception e){abort();}}
        private static void pump(InputStream input,OutputStream output)throws IOException {byte[] buffer=new byte[16384];int n;while((n=input.read(buffer))!=-1){if(n==0)throw new IOException("Stream made no progress.");output.write(buffer,0,n);}}
        private void done(){if(finished.incrementAndGet()==2){if(closed.compareAndSet(false,true)){quiet(stream);quiet(peer);/* The application owns its client endpoint until close. */}}}
        void abort(){if(closed.compareAndSet(false,true)){quiet(client);quiet(stream);quiet(peer);}}
    }
    private static void quiet(Closeable value){if(value!=null)try{value.close();}catch(IOException ignored){}}
}
