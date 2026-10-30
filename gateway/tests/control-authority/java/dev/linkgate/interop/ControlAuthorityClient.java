package dev.linkgate.interop;

import dev.linkgate.authority.AuthoritySnapshot;
import dev.linkgate.authority.ControlClient;
import dev.linkgate.authority.LeaseClock;
import dev.linkgate.authority.LeaseRejectedException;
import dev.linkgate.authority.PeerLeaseSource;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.transport.DeviceIdentity;
import dev.linkgate.transport.PeerClient;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.security.KeyPairGenerator;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.security.spec.ECGenParameterSpec;
import java.util.Map;

public final class ControlAuthorityClient {
    private static int checks;
    private static String phase = "prepare";
    private static void check(boolean value) { if (!value) throw new IllegalStateException(); checks++; }
    private static void waitFor(Path path) throws Exception {
        long deadline = System.nanoTime() + (path.getFileName().toString().equals("revoked") ? 100_000_000_000L : 30_000_000_000L);
        while (!Files.exists(path) || Files.size(path) == 0) { if (System.nanoTime() >= deadline) throw new IllegalStateException(); Thread.sleep(20); }
    }
    private static X509Certificate certificate(Path file) throws Exception { try (var stream = Files.newInputStream(file)) { return (X509Certificate)CertificateFactory.getInstance("X.509").generateCertificate(stream); } }
    public static void main(String[] args) {
        try { run(Path.of(args[0])); System.out.println("{\"passed\":true,\"checks\":" + checks + "}"); }
        catch (Throwable failure) { System.err.println("control-stage=" + phase + ";failure=" + failure.getClass().getSimpleName()); System.exit(1); }
    }
    private static void run(Path directory) throws Exception {
        var generator = KeyPairGenerator.getInstance("EC"); generator.initialize(new ECGenParameterSpec("secp256r1")); var key = generator.generateKeyPair();
        Files.writeString(directory.resolve("java.csr"), DeviceIdentity.request(key.getPrivate(), key.getPublic()), StandardOpenOption.CREATE_NEW);
        waitFor(directory.resolve("ready.json"));
        phase = "connect";
        Map<String,Object> config = WireJson.read(Files.readAllBytes(directory.resolve("ready.json")), 4096);
        int port = (int)WireJson.integer(config.get("port"), 1, 65535);
        X509Certificate root = certificate(directory.resolve("owner-root.pem")), server = certificate(directory.resolve("server-cert.pem"));
        DeviceIdentity identity = new DeviceIdentity(key.getPrivate(), certificate(directory.resolve("java-cert.pem")), root);
        var target = new AuthoritySnapshot.Target(WireJson.text(config.get("publisher"), 64), "org.example.publisher", "files");
        try (ControlClient client = ControlClient.connect(new byte[]{127,0,0,1}, port, identity, server, root, new LeaseClock())) {
            phase = "acquire";
            var authority = client.acquire("org.example.client", target);
            check(authority.receipt.active()); check(authority.receipt.authority.scope.visitorDevice.equals(identity.deviceId()));
            check(authority.receipt.authority.scope.target.equals(target)); check(authority.receipt.authority.authority.equals("files.example.invalid"));
            check(!authority.toString().contains(authority.token));
            Files.writeString(directory.resolve("java-token.jwt"), authority.token, StandardOpenOption.CREATE_NEW);
            check(client.check(java.util.List.of(authority.receipt))[0]);
            phase = "denied";
            try { client.acquire("org.example.other", target); throw new IllegalStateException(); }
            catch (ControlClient.Refused refused) { check(refused.code.equals("AUTH_REQUIRED") && refused.getCause() == null); }
            try { client.validate(authority.token, authority.receipt.authority.scope); throw new IllegalStateException(); }
            catch (LeaseRejectedException refused) { check(refused.getCause() == null); }
            phase = "wrong-pin";
            try (var ignored = ControlClient.connect(new byte[]{127,0,0,1}, port, identity, certificate(directory.resolve("publisher-cert.pem")), root, new LeaseClock())) { throw new IllegalStateException(); }
            catch (ControlClient.Refused refused) { check(refused.code.equals("CONTROL_UNAVAILABLE") && refused.getCause() == null); }
            try (var business = new BusinessFixture(client, identity, target.publisherDevice, directory)) {
                Files.writeString(directory.resolve("java-initial"), "true", StandardOpenOption.CREATE_NEW);
                phase = "revoked";
                waitFor(directory.resolve("revoked"));
                business.revoked();
                try { client.acquire("org.example.client", target); throw new IllegalStateException(); }
                catch (ControlClient.Refused refused) { check(refused.code.equals("AUTH_REQUIRED")); }
                check(!client.check(java.util.List.of(authority.receipt))[0]);
            }
            phase = "closed";
            client.close();
            try { client.acquire("org.example.client", target); throw new IllegalStateException(); }
            catch (LeaseRejectedException refused) { check(refused.getCause() == null); }
        }
    }

    private static final class BusinessFixture implements AutoCloseable {
        private final java.util.concurrent.ScheduledThreadPoolExecutor scheduler = new java.util.concurrent.ScheduledThreadPoolExecutor(2);
        private final java.util.concurrent.ThreadPoolExecutor controls = new java.util.concurrent.ThreadPoolExecutor(2,2,0,java.util.concurrent.TimeUnit.MILLISECONDS,new java.util.concurrent.ArrayBlockingQueue<>(16));
        private final java.util.concurrent.ExecutorService reader = java.util.concurrent.Executors.newSingleThreadExecutor();
        private final DeviceIdentity identity;
        private final AuthoritySnapshot.Target target;
        private final int port;
        private String saved;
        private PeerClient held;
        private java.util.concurrent.Future<Integer> blocked;
        BusinessFixture(ControlClient control, DeviceIdentity identity, String publisher, Path directory) throws Exception {
            this.identity=identity;target=new AuthoritySnapshot.Target(publisher,"org.example.publisher","stream");
            phase="peer-ready";waitFor(directory.resolve("peer.json"));
            port=(int)WireJson.integer(WireJson.read(Files.readAllBytes(directory.resolve("peer.json")),4096).get("port"),1,65535);
            try {
                phase="peer-denied";
                try(var ignored=open(null)){throw new IllegalStateException();}catch(PeerClient.Refused error){check(error.code.equals("AUTH_REQUIRED"));}
                try(var source=PeerLeaseSource.acquire(control,"org.example.client",target,"tcp",null);var ignored=open(source)){throw new IllegalStateException();}catch(PeerClient.Refused error){check(error.code.equals("AUTH_REQUIRED"));}
                Files.writeString(directory.resolve("java-local-denied"),"true",StandardOpenOption.CREATE_NEW);waitFor(directory.resolve("local-approved"));
                byte[] challenge=Files.readAllBytes(directory.resolve("application-challenge.bin"));check(challenge.length==32);
                phase="peer-file";
                try(var source=PeerLeaseSource.acquire(control,"org.example.client",target,"tcp",null);var peer=open(source)) {
                    peer.stream.output.write(1);peer.stream.output.write(challenge);peer.stream.finishWrites();
                    var digest=java.security.MessageDigest.getInstance("SHA-256");byte[] buffer=new byte[32768];long count=0;int n;
                    while((n=peer.stream.input.read(buffer))!=-1){digest.update(buffer,0,n);count+=n;if(count>67108864)throw new IllegalStateException();}
                    byte[] block=new byte[65536];for(int i=0;i<block.length;i++)block[i]=(byte)(i%251);var expected=java.security.MessageDigest.getInstance("SHA-256");for(int i=0;i<1024;i++)expected.update(block);
                    check(count==67108864&&java.security.MessageDigest.isEqual(digest.digest(),expected.digest()));peer.stream.drainWriteCompletion();
                }
                phase="peer-hold";
                saved=control.acquire("org.example.client",target).token;
                held=open(PeerLeaseSource.acquire(control,"org.example.client",target,"tcp",null));held.stream.output.write(2);held.stream.output.write(challenge);check(held.stream.input.read()==1);
                blocked=reader.submit(()->held.stream.input.read());
            }catch(Exception failure){close();throw failure;}
        }
        private PeerClient open(PeerClient.LeaseSource source)throws Exception {
            return PeerClient.open(identity,target.publisherDevice,java.net.InetAddress.getByAddress(new byte[]{127,0,0,1}),port,"org.example.client","org.example.publisher","stream","tcp",null,5000,scheduler,controls,System::nanoTime,PeerClient::connectTcp,new PeerClient.Cancellation(),source);
        }
        void revoked()throws Exception {
            phase="peer-revoked";
            try {check(blocked.get(5,java.util.concurrent.TimeUnit.SECONDS)==-1);}catch(java.util.concurrent.ExecutionException failure){check(failure.getCause() instanceof java.io.IOException);}
            // Bypass local acquisition deliberately: the publisher must reject a
            // still-signed captured token using current server authority as well.
            PeerClient.LeaseSource replay=new PeerClient.LeaseSource(){public String token(){return saved;}public void close(){}};
            try(var ignored=open(replay)){throw new IllegalStateException();}catch(PeerClient.Refused error){check(error.code.equals("AUTH_REQUIRED"));}
        }
        @Override public void close() {
            if(held!=null)held.abort();reader.shutdownNow();controls.shutdownNow();scheduler.shutdownNow();
            try{reader.awaitTermination(5,java.util.concurrent.TimeUnit.SECONDS);controls.awaitTermination(5,java.util.concurrent.TimeUnit.SECONDS);scheduler.awaitTermination(5,java.util.concurrent.TimeUnit.SECONDS);}catch(InterruptedException failure){Thread.currentThread().interrupt();}
        }
    }
}
