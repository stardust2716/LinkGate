package dev.linkgate.interop;

import dev.linkgate.contracts.WireJson;
import dev.linkgate.transport.DeviceIdentity;
import dev.linkgate.transport.PeerClient;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.security.KeyFactory;
import java.security.KeyPairGenerator;
import java.security.MessageDigest;
import java.security.PrivateKey;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.security.spec.ECGenParameterSpec;
import java.security.spec.PKCS8EncodedKeySpec;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.TimeUnit;

public final class InteropClient {
    private static final int FILE_BYTES=67108864;
    public static void main(String[] args)throws Exception {
        Path directory=Path.of(args[1]);
        if("request".equals(args[0])) {
            KeyPairGenerator generator=KeyPairGenerator.getInstance("EC");generator.initialize(new ECGenParameterSpec("secp256r1"));var pair=generator.generateKeyPair();
            Files.write(directory.resolve("device-key.pk8"),pair.getPrivate().getEncoded(),StandardOpenOption.CREATE_NEW);
            Files.writeString(directory.resolve("device-request.pem"),DeviceIdentity.request(pair.getPrivate(),pair.getPublic()),StandardOpenOption.CREATE_NEW);
            emit("event","request","deviceId",DeviceIdentity.keyId(pair.getPublic()));return;
        }
        if(!"run".equals(args[0])||args.length!=4)throw new IllegalArgumentException("Explicit fixture arguments required.");
        PrivateKey key=KeyFactory.getInstance("EC").generatePrivate(new PKCS8EncodedKeySpec(Files.readAllBytes(directory.resolve("device-key.pk8"))));
        DeviceIdentity identity=new DeviceIdentity(key,certificate(directory.resolve("device-cert.pem")),certificate(directory.resolve("owner-root.pem")));
        String expectedPeer=WireJson.key(args[2]);int port=Integer.parseInt(args[3]);
        ScheduledThreadPoolExecutor scheduler=new ScheduledThreadPoolExecutor(2);scheduler.setRemoveOnCancelPolicy(true);
        ThreadPoolExecutor controls=new ThreadPoolExecutor(0,8,10,TimeUnit.SECONDS,new SynchronousQueue<>());
        try(BufferedReader commands=new BufferedReader(new InputStreamReader(System.in,StandardCharsets.UTF_8))) {
            emit("event","ready","deviceId",identity.deviceId());
            String command;
            while((command=commands.readLine())!=null&&!command.equals("stop")) {
                if(command.equals("deny")||command.equals("wrong-peer")||command.equals("after-revoke")) {
                    boolean denied=false;
                    try(PeerClient peer=open(identity,command.equals("wrong-peer")?"0".repeat(64):expectedPeer,port,scheduler,controls)){ }
                    catch(java.io.IOException|java.security.GeneralSecurityException e){denied=true;}
                    if(!denied)throw new AssertionError("Unauthorized transport accepted.");emit("event",command,"denied",true);
                }else if(command.equals("file")) {
                    long began=System.nanoTime();
                    try(PeerClient peer=open(identity,expectedPeer,port,scheduler,controls)) {
                        byte[] token=System.getenv("LINKGATE_INTEROP_TOKEN").getBytes(StandardCharsets.US_ASCII);if(token.length!=64)throw new AssertionError();
                        peer.stream.output.write(token);peer.stream.finishWrites();
                        MessageDigest digest=MessageDigest.getInstance("SHA-256");byte[] buffer=new byte[32768];int count=0,n;
                        while((n=peer.stream.input.read(buffer))!=-1){count+=n;if(count>FILE_BYTES)throw new AssertionError("Unexpected file length.");digest.update(buffer,0,n);}
                        byte[] block=new byte[65536];for(int i=0;i<block.length;i++)block[i]=(byte)(i%251);
                        MessageDigest expected=MessageDigest.getInstance("SHA-256");for(int i=0;i<FILE_BYTES/block.length;i++)expected.update(block);
                        if(count!=FILE_BYTES||!MessageDigest.isEqual(digest.digest(),expected.digest()))throw new AssertionError("File integrity failed.");peer.stream.drainWriteCompletion();
                        emit("event","file","bytes",count,"integrity",true,"halfClose",true,"elapsedMs",TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-began));
                    }
                }else if(command.equals("hold")) {
                    try(PeerClient peer=open(identity,expectedPeer,port,scheduler,controls)) {
                        emit("event","holding");long began=System.nanoTime();boolean ended=false;
                        try{peer.stream.input.read();}catch(java.io.IOException e){ended=true;}
                        if(!ended)throw new AssertionError("Revoked transport reported clean FIN.");emit("event","revoked","elapsedMs",TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-began));
                    }
                }else throw new IllegalArgumentException("Unknown fixture command.");
            }
        }finally{scheduler.shutdownNow();controls.shutdownNow();scheduler.awaitTermination(5,TimeUnit.SECONDS);controls.awaitTermination(5,TimeUnit.SECONDS);}
    }
    private static PeerClient open(DeviceIdentity identity,String peer,int port,ScheduledThreadPoolExecutor scheduler,ThreadPoolExecutor controls)throws Exception {
        return PeerClient.open(identity,peer,InetAddress.getByAddress(new byte[]{127,0,0,1}),port,"org.example.java-transport","org.example.interop-publisher","file","tcp",null,5000,scheduler,controls,System::nanoTime,PeerClient::connectTcp,new PeerClient.Cancellation());
    }
    private static X509Certificate certificate(Path path)throws Exception {try(var input=Files.newInputStream(path)){return (X509Certificate)CertificateFactory.getInstance("X.509").generateCertificate(input);}}
    private static void emit(Object... fields){System.out.println(new String(WireJson.write(WireJson.map(fields),65536),StandardCharsets.UTF_8));}
}
