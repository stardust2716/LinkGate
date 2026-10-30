package com.nebula.linkgate;

import android.net.TrafficStats;
import android.util.Log;
import org.json.JSONObject;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Arrays;
import java.util.Base64;
import java.util.concurrent.atomic.AtomicLong;

/** Explicitly user-started, loopback-only fixture test. Never scans other services. */
final class NetworkTest {
    static volatile String result = "尚未运行网络测试";
    static volatile boolean running;
    private static final SecureRandom random = new SecureRandom();
    static synchronized void start(int port, String scenario) {
        if(running) return;
        running=true; result="测试运行中（60 秒）…";
        new Thread(()->run(port,scenario),"linkgate-network-test").start();
    }
    private static Socket connect(int port) throws Exception {
        Socket s=new Socket();
        try {s.connect(new InetSocketAddress("127.0.0.1",port),10000);s.setSoTimeout(15000);return s;}
        catch(Exception e){s.close();throw e;}
    }
    private static String header(InputStream in) throws Exception {
        ByteArrayOutputStream b=new ByteArrayOutputStream(); int state=0;
        while(b.size()<8192){int c=in.read();if(c<0)throw new EOFException();b.write(c);
            state=(state==0&&c==13)?1:(state==1&&c==10)?2:(state==2&&c==13)?3:(state==3&&c==10)?4:0;
            if(state==4)return b.toString("US-ASCII");}
        throw new IOException("header_limit");
    }
    private static void run(int port,String scenario) {
        long start=System.currentTimeMillis(),end=System.nanoTime()+60000000000L;
        int uid=android.os.Process.myUid(); long rx=TrafficStats.getUidRxBytes(uid),tx=TrafficStats.getUidTxBytes(uid);
        JSONObject before=GatewayService.snapshot(); AtomicLong wsBytes=new AtomicLong(),wsFrames=new AtomicLong();
        String[] wsError={"none"}; AtomicLong wsControls=new AtomicLong(); long fileBytes=0,files=0; String fileError="none";
        Thread websocket=new Thread(()->{
            try(Socket s=connect(port)) {
                InputStream in=s.getInputStream(); OutputStream out=s.getOutputStream();
                byte[] key=new byte[16];random.nextBytes(key);String encoded=Base64.getEncoder().encodeToString(key);
                out.write(("GET /ws HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: "+encoded+"\r\n\r\n").getBytes(StandardCharsets.US_ASCII));out.flush();
                String response=header(in);
                String accept=Base64.getEncoder().encodeToString(MessageDigest.getInstance("SHA-1").digest((encoded+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11").getBytes(StandardCharsets.US_ASCII)));
                int accepts=0;
                for(String row:response.split("\r\n")){String[] field=row.split(":",2);if(field.length==2&&field[0].equalsIgnoreCase("Sec-WebSocket-Accept")){if(!field[1].trim().equals(accept))throw new IOException("protocol_error");accepts++;}}
                if(!response.startsWith("HTTP/1.1 101 ")||accepts!=1)throw new IOException("protocol_error");
                byte[] payload=new byte[1024];for(int i=0;i<payload.length;i++)payload[i]=(byte)(i%251);
                ProbeWebSocket probe=new ProbeWebSocket(in,out);
                while(System.nanoTime()<end){
                    probe.sendBinary(payload);
                    byte[] reply=probe.readBinary(payload.length);
                    if(!Arrays.equals(payload,reply))throw new IOException("integrity_failed");
                    wsBytes.addAndGet(2048);wsFrames.incrementAndGet();Thread.sleep(50);
                }
                s.setSoTimeout(5000);probe.closeNormally();wsControls.set(probe.controlFrames);
            } catch(Exception e){wsError[0]=e instanceof SocketTimeoutException?"timeout":Arrays.asList("protocol_error","peer_closed","integrity_failed").contains(e.getMessage())?e.getMessage():"websocket_failed";}
        },"linkgate-ws-test");websocket.start();
        try {
            while(System.nanoTime()<end){
                try(Socket s=connect(port)) {
                    s.getOutputStream().write("GET /bytes/1048576 HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n".getBytes(StandardCharsets.US_ASCII));
                    InputStream in=s.getInputStream();String h=header(in);
                    if(!h.startsWith("HTTP/1.1 200 ")||!h.contains("Content-Length: 1048576\r\n"))throw new IOException("http");
                    byte[] buffer=new byte[32768]; int offset=0,n;
                    while(offset<1048576&&(n=in.read(buffer,0,Math.min(buffer.length,1048576-offset)))>0){
                        for(int i=0;i<n;i++)if(buffer[i]!=(byte)((offset+i)%251))throw new IOException("integrity");
                        offset+=n;fileBytes+=n;
                    }
                    if(offset!=1048576)throw new EOFException();files++;
                }
            }
        }catch(Exception e){fileError=e instanceof SocketTimeoutException?"timeout":"file_failed";}
        try {websocket.join(Math.max(20000,(end-System.nanoTime())/1000000+20000));}catch(InterruptedException e){Thread.currentThread().interrupt();}
        try {
            // Final FRP closed sample may follow socket EOF slightly.
            Thread.sleep(1200);
            JSONObject report=new JSONObject().put("testVersion",2).put("scenario",scenario).put("startedUtcMs",start)
                .put("elapsedMs",System.currentTimeMillis()-start).put("fileBytesVerified",fileBytes).put("filesCompleted",files)
                .put("fileError",fileError).put("websocketPayloadBytes",wsBytes.get()).put("websocketFrames",wsFrames.get()).put("websocketError",wsError[0])
                .put("websocketControlFrames",wsControls.get())
                .put("uidRxDelta",rx<0?-1:TrafficStats.getUidRxBytes(uid)-rx).put("uidTxDelta",tx<0?-1:TrafficStats.getUidTxBytes(uid)-tx)
                .put("before",before).put("after",GatewayService.snapshot());
            result=report.toString(2);Log.i("LinkGateTest",report.toString());
        }catch(Exception e){result="测试报告生成失败";}
        finally{running=false;}
    }
    private NetworkTest(){}
}
