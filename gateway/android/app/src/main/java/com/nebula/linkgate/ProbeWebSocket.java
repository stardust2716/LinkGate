package com.nebula.linkgate;

import java.io.*;
import java.security.SecureRandom;

/** Bounded binary echo client for the test fixture, including RFC6455 controls. */
final class ProbeWebSocket {
    private final DataInputStream in;
    private final OutputStream out;
    private final SecureRandom random = new SecureRandom();
    int controlFrames;
    ProbeWebSocket(InputStream input, OutputStream output) {in=new DataInputStream(input);out=output;}
    void sendBinary(byte[] body) throws IOException {send(2,body);}
    private void send(int opcode,byte[] body) throws IOException {
        if(body.length>65535)throw new IOException("protocol_error");
        ByteArrayOutputStream frame=new ByteArrayOutputStream(body.length+8);
        frame.write(0x80|opcode);
        if(body.length<126)frame.write(0x80|body.length);
        else {frame.write(0xfe);frame.write(body.length>>8);frame.write(body.length&255);}
        byte[] mask=new byte[4];random.nextBytes(mask);frame.write(mask);
        for(int i=0;i<body.length;i++)frame.write(body[i]^mask[i%4]);
        // One socket write, rather than one syscall for every payload byte.
        out.write(frame.toByteArray());out.flush();
    }
    private static final class Frame {
        int opcode;boolean fin;byte[] body;
        Frame(int op,boolean f,byte[] b){opcode=op;fin=f;body=b;}
    }
    private Frame read() throws IOException {
        int a=in.readUnsignedByte(),b=in.readUnsignedByte();
        if((a&0x70)!=0||(b&0x80)!=0)throw new IOException("protocol_error");
        int opcode=a&15;boolean fin=(a&128)!=0;long size=b&127;
        if(size==126){size=in.readUnsignedShort();if(size<126)throw new IOException("protocol_error");}
        else if(size==127){size=in.readLong();if(size<65536)throw new IOException("protocol_error");}
        if(size>65536||size<0||(opcode>=8&&(!fin||size>125)))throw new IOException("protocol_error");
        byte[] body=new byte[(int)size];in.readFully(body);return new Frame(opcode,fin,body);
    }
    byte[] readBinary(int limit) throws IOException {
        ByteArrayOutputStream message=new ByteArrayOutputStream();boolean started=false;
        for(int count=0;count<256;count++) {
            Frame f=read();
            if(f.opcode>=8){
                controlFrames++;
                if(f.opcode==9){send(10,f.body);continue;}
                if(f.opcode==10)continue;
                if(f.opcode==8){send(8,f.body);throw new IOException("peer_closed");}
                throw new IOException("protocol_error");
            }
            if((!started&&f.opcode!=2)||(started&&f.opcode!=0)||message.size()+f.body.length>limit)throw new IOException("protocol_error");
            started=true;message.write(f.body);
            if(f.fin)return message.toByteArray();
        }
        throw new IOException("protocol_error");
    }
    void closeNormally() throws IOException {
        send(8,new byte[]{3,(byte)232});
        for(int count=0;count<64;count++) {
            Frame f=read();controlFrames++;
            if(f.opcode==8)return;
            if(f.opcode==9)send(10,f.body);
            else if(f.opcode!=10)throw new IOException("protocol_error");
        }
        throw new IOException("protocol_error");
    }
}
