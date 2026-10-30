package dev.linkgate.transport;

import java.io.Closeable;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.Arrays;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.locks.ReentrantLock;

public final class BusinessChannel implements Closeable {
    public static final int MAX_PAYLOAD=16384;
    private final InputStream source;private final OutputStream sink;private final Closeable transport;
    private final ReentrantLock reading=new ReentrantLock(),writing=new ReentrantLock(),wireWriting=new ReentrantLock();
    private volatile boolean finished,closed;
    private boolean eof,acknowledged;private byte[] buffered=new byte[0];private int offset;
    private final AtomicLong received=new AtomicLong(),sent=new AtomicLong();
    public BusinessChannel(InputStream input,OutputStream output,Closeable transport) {this.source=input;this.sink=output;this.transport=transport;}
    public final InputStream input=new InputStream() {
        @Override public int read()throws IOException{byte[] one=new byte[1];return read(one,0,1)==-1?-1:one[0]&255;}
        @Override public int read(byte[] buffer,int start,int length)throws IOException {
            if(start<0||length<0||start>buffer.length-length)throw new IndexOutOfBoundsException();if(length==0)return 0;requireOpen();
            if(!reading.tryLock())throw new IOException("Concurrent business read.");
            try {
                if(eof)return -1;
                while(offset==buffered.length) {
                    byte[] frame=readFrame(source,MAX_PAYLOAD+1);
                    if(frame.length==1&&frame[0]==4){acknowledge();continue;}
                    if(frame.length==1&&frame[0]==2){eof=true;buffered=new byte[0];writeWire(new byte[]{4});return -1;}
                    if(frame[0]==3)throw new IOException("Business stream closed.");
                    if(frame.length<2||frame[0]!=1)throw new IOException("Invalid business frame.");buffered=frame;offset=1;
                }
                int n=Math.min(length,buffered.length-offset);System.arraycopy(buffered,offset,buffer,start,n);offset+=n;received.addAndGet(n);return n;
            }finally{reading.unlock();}
        }
        @Override public void close()throws IOException{BusinessChannel.this.close();}
    };
    public final OutputStream output=new OutputStream() {
        @Override public void write(int value)throws IOException{write(new byte[]{(byte)value});}
        @Override public void write(byte[] bytes,int start,int length)throws IOException {
            if(start<0||length<0||start>bytes.length-length)throw new IndexOutOfBoundsException();requireOpen();
            if(!writing.tryLock())throw new IOException("Concurrent business write.");
            try {
                if(finished)throw new IOException("Business write direction closed.");
                while(length>0){int n=Math.min(length,MAX_PAYLOAD);byte[] frame=new byte[n+1];frame[0]=1;System.arraycopy(bytes,start,frame,1,n);writeWire(frame);sent.addAndGet(n);start+=n;length-=n;}
            }finally{writing.unlock();}
        }
        @Override public void flush()throws IOException{requireOpen();wireWriting.lock();try{sink.flush();}finally{wireWriting.unlock();}}
        @Override public void close()throws IOException{finishWrites();}
    };
    public long receivedBytes(){return received.get();}public long sentBytes(){return sent.get();}
    public void finishWrites()throws IOException {
        requireOpen();if(!writing.tryLock())throw new IOException("Concurrent business write.");
        try{if(!finished){finished=true;writeWire(new byte[]{2});}}finally{writing.unlock();}
    }
    public void drainWriteCompletion()throws IOException {
        requireOpen();if(!reading.tryLock())throw new IOException("Concurrent business read.");
        try{if(!eof||!finished)throw new IOException("Both directions must be finished.");if(!acknowledged){byte[] frame=readFrame(source,MAX_PAYLOAD+1);if(!Arrays.equals(frame,new byte[]{4}))throw new IOException("Invalid FIN acknowledgement.");acknowledge();}}finally{reading.unlock();}
    }
    private void acknowledge()throws IOException {if(!finished||acknowledged)throw new IOException("Unexpected FIN acknowledgement.");acknowledged=true;}
    private void writeWire(byte[] frame)throws IOException {wireWriting.lock();try{requireOpen();writeFrame(sink,frame,MAX_PAYLOAD+1);}finally{wireWriting.unlock();}}
    private void requireOpen()throws IOException {if(closed)throw new IOException("Business channel closed.");}
    @Override public void close()throws IOException {if(closed)return;closed=true;transport.close();}
    public static byte[] readFrame(InputStream input,int limit)throws IOException {
        byte[] header=new byte[4];readExactly(input,header);long length=(header[0]&255L)|((header[1]&255L)<<8)|((header[2]&255L)<<16)|((header[3]&255L)<<24);
        if(length<1||length>limit)throw new IOException("Frame exceeds limit.");byte[] result=new byte[(int)length];readExactly(input,result);return result;
    }
    public static void writeFrame(OutputStream output,byte[] payload,int limit)throws IOException {
        int n=payload.length;if(n<1||n>limit)throw new IOException("Frame exceeds limit.");output.write(new byte[]{(byte)n,(byte)(n>>>8),(byte)(n>>>16),(byte)(n>>>24)});output.write(payload);output.flush();
    }
    private static void readExactly(InputStream input,byte[] value)throws IOException {
        int offset=0;while(offset<value.length){int n=input.read(value,offset,value.length-offset);if(n<0)throw new EOFException("Business transport interrupted.");if(n==0)throw new IOException("Transport made no progress.");offset+=n;}
    }
}
