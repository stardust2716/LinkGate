package dev.linkgate.agent.android;

import android.content.Context;
import android.util.AtomicFile;
import dev.linkgate.agent.AgentRegistry;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.transport.DeviceIdentity;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.net.InetAddress;
import java.security.cert.X509Certificate;
import java.util.Map;

final class ServerSettings {
    final String host,certificatePem;
    final int controlPort,backendPort;
    final X509Certificate certificate;
    final java.util.List<String> stunEndpoints;
    private final byte[] document;
    private ServerSettings(byte[] bytes,AndroidIdentity identity)throws Exception {
        Map<String,Object> value=WireJson.read(bytes,65536);if(value.containsKey("stunEndpoints"))WireJson.fields(value,"schema","host","controlPort","backendPort","serverCertificate","stunEndpoints");else WireJson.fields(value,"schema","host","controlPort","backendPort","serverCertificate");
        stunEndpoints=dev.linkgate.transport.StunEndpoints.snapshot(value.containsKey("stunEndpoints")?value.get("stunEndpoints"):java.util.Collections.emptyList());
        WireJson.integer(value.get("schema"),1,1);host=AgentRegistry.ipv4(WireJson.text(value.get("host"),15));
        controlPort=(int)WireJson.integer(value.get("controlPort"),1,65535);backendPort=(int)WireJson.integer(value.get("backendPort"),1,65535);
        if(controlPort==backendPort)throw new IOException("Separate server endpoints required.");
        if(!(value.get("serverCertificate") instanceof String)||((String)value.get("serverCertificate")).getBytes(java.nio.charset.StandardCharsets.UTF_8).length>49152)throw new IOException("Bounded public certificate required.");certificatePem=(String)value.get("serverCertificate");certificate=AndroidIdentity.parse(certificatePem);DeviceIdentity.validateDevice(certificate,identity.root());document=WireJson.write(value,65536);
    }
    static ServerSettings parse(byte[] bytes,AndroidIdentity identity)throws Exception{return new ServerSettings(bytes,identity);}
    InetAddress address()throws IOException {String[] fields=host.split("\\.");byte[] bytes=new byte[4];for(int i=0;i<4;i++)bytes[i]=(byte)Integer.parseInt(fields[i]);return InetAddress.getByAddress(bytes);}
    private static AtomicFile file(Context context)throws IOException {
        File root=context.getNoBackupFilesDir().getCanonicalFile(),base=new File(root,"server-v1.json");
        for(String suffix:new String[]{"",".bak",".new"}){File part=new File(base.getPath()+suffix);if(!part.getCanonicalFile().equals(part.getAbsoluteFile()))throw new IOException("Linked server configuration rejected.");}return new AtomicFile(base);
    }
    static ServerSettings load(Context context,AndroidIdentity identity)throws Exception {
        AtomicFile file=file(context);boolean base=file.getBaseFile().exists(),backup=new File(file.getBaseFile().getPath()+".bak").exists();
        if(!base&&!backup){if(new File(file.getBaseFile().getPath()+".new").exists())throw new IOException("Incomplete server configuration.");return null;}
        try(var input=file.openRead()){ByteArrayOutputStream output=new ByteArrayOutputStream();byte[] chunk=new byte[4096];int n;while((n=input.read(chunk))!=-1){if(output.size()+n>65536)throw new IOException("Server configuration exceeds bound.");output.write(chunk,0,n);}return parse(output.toByteArray(),identity);}
    }
    void install(Context context)throws Exception {
        if(context.getSharedPreferences("owner",Context.MODE_PRIVATE).getBoolean("enabled",false)||GatewayService.startingOrRunning())throw new IllegalStateException("OWNER_STOP_REQUIRED");
        AtomicFile file=file(context);FileOutputStream output=null;
        try{output=file.startWrite();output.write(document);output.getFD().sync();file.finishWrite(output);}catch(Exception failure){if(output!=null)file.failWrite(output);throw failure;}
    }
}
