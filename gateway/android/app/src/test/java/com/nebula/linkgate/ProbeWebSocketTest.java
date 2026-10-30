package com.nebula.linkgate;
import org.junit.Test;
import static org.junit.Assert.*;
import java.io.*;

public class ProbeWebSocketTest {
    private byte[] maskedPayload(byte[] bytes,int expectedOpcode) throws Exception {
        DataInputStream in=new DataInputStream(new ByteArrayInputStream(bytes));
        assertEquals(0x80|expectedOpcode,in.readUnsignedByte());int n=in.readUnsignedByte();assertTrue((n&128)!=0);n&=127;
        if(n==126)n=in.readUnsignedShort();
        byte[] mask=new byte[4];in.readFully(mask);byte[] payload=new byte[n];in.readFully(payload);
        for(int i=0;i<n;i++)payload[i]^=mask[i%4];return payload;
    }
    @Test public void handlesUnsolicitedPongAndPingBetweenFragments() throws Exception {
        // binary non-final "ab", Pong, Ping "x", final continuation "cd"
        byte[] frames={2,2,97,98,(byte)0x8a,0,(byte)0x89,1,120,(byte)0x80,2,99,100};
        InputStream shortReads=new ByteArrayInputStream(frames){
            @Override public synchronized int read(byte[] b,int off,int len){return super.read(b,off,Math.min(len,1));}
        };
        ByteArrayOutputStream out=new ByteArrayOutputStream();ProbeWebSocket p=new ProbeWebSocket(shortReads,out);
        assertArrayEquals(new byte[]{97,98,99,100},p.readBinary(4));assertEquals(2,p.controlFrames);
        assertArrayEquals(new byte[]{120},maskedPayload(out.toByteArray(),10));
    }
    @Test public void batchesClientFrameAndClosesNormally() throws Exception {
        final int[] writes={0};ByteArrayOutputStream out=new ByteArrayOutputStream(){@Override public synchronized void write(byte[] b,int o,int n){writes[0]++;super.write(b,o,n);}};
        ProbeWebSocket p=new ProbeWebSocket(new ByteArrayInputStream(new byte[]{(byte)0x88,2,3,(byte)232}),out);
        byte[] body=new byte[1024];for(int i=0;i<body.length;i++)body[i]=(byte)i;
        p.sendBinary(body);assertArrayEquals(body,maskedPayload(out.toByteArray(),2));assertEquals(1,writes[0]);
        out.reset();p.closeNormally();assertArrayEquals(new byte[]{3,(byte)232},maskedPayload(out.toByteArray(),8));
    }
    @Test public void rejectsInvalidFramesAndOversizedMessages() throws Exception {
        for(byte[] frame:new byte[][]{{(byte)0x82,(byte)0x80},{9,0},{(byte)0x80,0},{(byte)0x82,3,1,2,3},{(byte)0x82,126,0,2,1,2}}){
            try{new ProbeWebSocket(new ByteArrayInputStream(frame),new ByteArrayOutputStream()).readBinary(2);fail("invalid frame accepted");}
            catch(IOException expected){}
        }
    }
}
