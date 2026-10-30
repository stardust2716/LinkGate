package dev.linkgate.transport;

import java.io.IOException;
import java.net.InetAddress;
import java.util.Collections;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.Test;
import static org.junit.Assert.*;

public final class PeerOpeningTest {
    @Test public void remainingBudgetIsNotResetOrRejectedAfterPreparation()throws Exception {
        var scheduler=Executors.newSingleThreadScheduledExecutor();AtomicInteger remaining=new AtomicInteger();var cancellation=new PeerClient.Cancellation();
        try {
            try {
                PeerClient.open(null,String.join("",Collections.nCopies(64,"1")),InetAddress.getByAddress(new byte[]{127,0,0,1}),17443,"org.example.client","org.example.publisher","file","tcp",null,999,scheduler,Runnable::run,()->0,(address,port,budget,owner)->{remaining.set(budget);throw new IOException("fixture connector refuses before identity use");},cancellation);
                fail("Connector failure ignored.");
            }catch(IOException expected){assertEquals(999,remaining.get());assertTrue(cancellation.isClosed());}
            try {
                PeerClient.open(null,String.join("",Collections.nCopies(64,"1")),InetAddress.getByAddress(new byte[]{127,0,0,1}),17443,"org.example.client","org.example.publisher","file","tcp",null,0,scheduler,Runnable::run,()->0,(address,port,budget,owner)->{fail("Exhausted budget dialed.");throw new IOException();},new PeerClient.Cancellation());
                fail("Exhausted budget accepted.");
            }catch(IllegalArgumentException expected){}
        }finally{scheduler.shutdownNow();}
    }
}
