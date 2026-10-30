package dev.linkgate.streams;

import android.os.ParcelFileDescriptor;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.descriptors.DescriptorStream;
import dev.linkgate.descriptors.DescriptorWire;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.Map;

final class DescriptorDuplex implements SocketAdapter.Duplex {
    private final DescriptorStream stream;
    DescriptorDuplex(ParcelFileDescriptor descriptor,Object lifetime)throws IOException {stream=new DescriptorStream(descriptor,lifetime);}
    void attach()throws IOException {
        DescriptorWire.write(stream.output(),WireJson.map("kind","attach"));Map<String,Object> ready=DescriptorWire.read(stream.input());
        try{WireJson.fields(ready,"kind");if(!"ready".equals(ready.get("kind")))throw new IOException("Descriptor attachment rejected.");}catch(RuntimeException e){throw new IOException("Invalid descriptor attachment.");}
    }
    @Override public InputStream input(){return stream.input();}
    @Override public OutputStream output(){return stream.output();}
    @Override public void finishWrites()throws IOException {stream.finishWrites();}
    @Override public void close()throws IOException {stream.close();}
}
