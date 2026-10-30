package dev.linkgate.descriptors;

import android.os.ParcelFileDescriptor;
import android.system.Os;
import android.system.OsConstants;
import java.io.Closeable;
import java.io.IOException;
import java.io.InputStream;
import java.io.FilterInputStream;
import java.io.OutputStream;
import java.util.concurrent.atomic.AtomicBoolean;

public final class DescriptorStream implements Closeable {
    private final ParcelFileDescriptor descriptor;private final Object lifetime;
    private ParcelFileDescriptor.AutoCloseInputStream input;private ParcelFileDescriptor.AutoCloseOutputStream output;
    private InputStream checkedInput;
    private final AtomicBoolean closed=new AtomicBoolean(),finished=new AtomicBoolean();
    public DescriptorStream(ParcelFileDescriptor descriptor,Object lifetime)throws IOException {
        this.descriptor=descriptor;this.lifetime=lifetime;
        try{input=new ParcelFileDescriptor.AutoCloseInputStream(ParcelFileDescriptor.dup(descriptor.getFileDescriptor()));output=new ParcelFileDescriptor.AutoCloseOutputStream(ParcelFileDescriptor.dup(descriptor.getFileDescriptor()));checkedInput=new FilterInputStream(input){@Override public int read()throws IOException{int value=in.read();if(value<0)descriptor.checkError();return value;}@Override public int read(byte[] bytes,int offset,int length)throws IOException{int n=in.read(bytes,offset,length);if(n<0)descriptor.checkError();return n;}};}
        catch(IOException e){close();throw e;}
    }
    public InputStream input(){return checkedInput;}public OutputStream output(){return output;}
    public void finishWrites()throws IOException {
        if(closed.get())throw new IOException("Stream closed.");if(finished.compareAndSet(false,true))try{Os.shutdown(descriptor.getFileDescriptor(),OsConstants.SHUT_WR);}catch(Exception e){throw new IOException("Stream half-close failed.");}
    }
    @Override public void close()throws IOException {
        if(!closed.compareAndSet(false,true))return;
        try{Os.shutdown(descriptor.getFileDescriptor(),OsConstants.SHUT_RDWR);}catch(Exception ignored){}
        try{if(input!=null)input.close();}finally{try{if(output!=null)output.close();}finally{descriptor.close();}}
    }
    public void abort()throws IOException {
        if(!closed.compareAndSet(false,true))return;java.io.FileDescriptor raw=null;try{if(input!=null)raw=input.getFD();}catch(IOException ignored){}
        try{descriptor.closeWithError("Stream interrupted.");}finally{if(raw!=null)try{Os.shutdown(raw,OsConstants.SHUT_RDWR);}catch(Exception ignored){}try{if(input!=null)input.close();}finally{try{if(output!=null)output.close();}finally{descriptor.close();}}}
    }
}
