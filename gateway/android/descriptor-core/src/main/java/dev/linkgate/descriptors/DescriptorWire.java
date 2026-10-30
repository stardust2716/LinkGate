package dev.linkgate.descriptors;

import dev.linkgate.contracts.WireJson;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.Map;

public final class DescriptorWire {
    private DescriptorWire() { }
    public static Map<String,Object> read(InputStream input)throws IOException {
        byte[] header=new byte[4];exact(input,header);long length=(header[0]&255L)|((header[1]&255L)<<8)|((header[2]&255L)<<16)|((header[3]&255L)<<24);
        if(length<1||length>65536)throw new IOException("Invalid descriptor frame.");byte[] bytes=new byte[(int)length];exact(input,bytes);
        try{return WireJson.read(bytes,65536);}catch(RuntimeException e){throw new IOException("Invalid descriptor message.");}
    }
    public static void write(OutputStream output,Object value)throws IOException {byte[] bytes=WireJson.write(value,65536);int n=bytes.length;output.write(new byte[]{(byte)n,(byte)(n>>>8),(byte)(n>>>16),(byte)(n>>>24)});output.write(bytes);output.flush();}
    private static void exact(InputStream input,byte[] buffer)throws IOException {int offset=0;while(offset<buffer.length){int n=input.read(buffer,offset,buffer.length-offset);if(n<=0)throw new IOException("Descriptor transport interrupted.");offset+=n;}}
}
