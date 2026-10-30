package dev.linkgate.transport;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import org.junit.Test;
import static org.junit.Assert.*;

public class BusinessChannelTest {
    @Test public void fragmentedFramesAndDirectionalFin()throws Exception {
        ByteArrayOutputStream encoded=new ByteArrayOutputStream();BusinessChannel writer=new BusinessChannel(new ByteArrayInputStream(new byte[0]),encoded,()->{});
        byte[] payload=new byte[40000];for(int i=0;i<payload.length;i++)payload[i]=(byte)i;writer.output.write(payload);writer.finishWrites();
        ByteArrayInputStream original=new ByteArrayInputStream(encoded.toByteArray());InputStream fragmented=new InputStream(){@Override public int read(){return original.read();}@Override public int read(byte[] b,int o,int n){return original.read(b,o,Math.min(n,3));}};
        ByteArrayOutputStream acknowledgements=new ByteArrayOutputStream();BusinessChannel reader=new BusinessChannel(fragmented,acknowledgements,()->{});
        ByteArrayOutputStream result=new ByteArrayOutputStream();byte[] buffer=new byte[113];int n;while((n=reader.input.read(buffer))!=-1)result.write(buffer,0,n);
        assertArrayEquals(payload,result.toByteArray());assertEquals(40000,reader.receivedBytes());assertEquals(40000,writer.sentBytes());
        assertArrayEquals(new byte[]{1,0,0,0,4},acknowledgements.toByteArray());
        reader.output.write(new byte[]{9});assertEquals(1,reader.sentBytes());
    }
    @Test public void rejectsOversizedHeaderBeforePayload()throws Exception {
        byte[] bytes=new byte[65540];bytes[2]=1;ByteArrayInputStream input=new ByteArrayInputStream(bytes);
        BusinessChannel channel=new BusinessChannel(input,new ByteArrayOutputStream(),()->{});
        try{channel.input.read();fail();}catch(IOException expected){assertEquals(65536,input.available());}
    }
    @Test public void transportEofIsNotDirectionalFin()throws Exception {
        BusinessChannel channel=new BusinessChannel(new ByteArrayInputStream(new byte[0]),new ByteArrayOutputStream(),()->{});
        try{channel.input.read();fail();}catch(IOException expected){assertNotNull(expected.getMessage());}
    }
    @Test public void finAcknowledgementRequiresOurFin()throws Exception {
        BusinessChannel channel=new BusinessChannel(new ByteArrayInputStream(new byte[]{1,0,0,0,4}),new ByteArrayOutputStream(),()->{});
        try{channel.input.read();fail();}catch(IOException expected){assertEquals("Unexpected FIN acknowledgement.",expected.getMessage());}
    }
    @Test public void finAllowsReadResponseAndFinalDrain()throws Exception {
        ByteArrayOutputStream sink=new ByteArrayOutputStream();BusinessChannel channel=new BusinessChannel(new ByteArrayInputStream(new byte[]{1,0,0,0,4,2,0,0,0,1,7,1,0,0,0,2}),sink,()->{});
        channel.finishWrites();assertEquals(7,channel.input.read());assertEquals(-1,channel.input.read());channel.drainWriteCompletion();
        try{channel.output.write(9);fail();}catch(IOException expected){assertEquals("Business write direction closed.",expected.getMessage());}
    }
}
