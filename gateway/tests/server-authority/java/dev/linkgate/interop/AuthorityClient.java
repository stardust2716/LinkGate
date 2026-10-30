package dev.linkgate.interop;

import dev.linkgate.authority.AuthoritySnapshot;
import dev.linkgate.authority.LeaseClock;
import dev.linkgate.authority.LeaseRejectedException;
import dev.linkgate.authority.ServerLease;
import dev.linkgate.contracts.WireJson;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.util.Map;
import java.util.UUID;

public final class AuthorityClient {
    private static int checks;
    private static String stage="inputs";
    private interface Work{void run()throws Exception;}
    private static void check(boolean value){if(!value)throw new AssertionError("Server authority conformance failed.");checks++;}
    private static void denied(Work work)throws Exception{try{work.run();throw new AssertionError("Invalid server lease accepted.");}catch(LeaseRejectedException expected){check(expected.getCause()==null&&expected.code.equals("AUTH_REQUIRED"));}}
    private static X509Certificate certificate(Path path)throws Exception{try(var input=Files.newInputStream(path)){return (X509Certificate)CertificateFactory.getInstance("X.509").generateCertificate(input);}}
    public static void main(String[] arguments){try{run(arguments);}catch(Throwable failure){System.err.println("authority-stage="+stage+";failure="+failure.getClass().getSimpleName());System.exit(1);}}
    private static void run(String[] arguments)throws Exception {
        if(arguments.length!=1)throw new IllegalArgumentException("Fixture directory required.");Path directory=Path.of(arguments[0]);Map<String,Object> meta=WireJson.read(Files.readAllBytes(directory.resolve("metadata.json")),65536);String issuer=WireJson.key(meta.get("issuer"));UUID epoch=WireJson.uuid(meta.get("epoch"));long issued=WireJson.integer(meta.get("issuedUnix"),1,Long.MAX_VALUE)*1000L;
        X509Certificate leaf=certificate(directory.resolve("server-cert.pem")),root=certificate(directory.resolve("owner-root.pem"));byte[] snapshot=Files.readAllBytes(directory.resolve("snapshot.json"));AuthoritySnapshot current=AuthoritySnapshot.parse(snapshot);String first=Files.readString(directory.resolve("first.jwt")),next=Files.readString(directory.resolve("next.jwt"));LeaseClock live=new LeaseClock();stage="valid";check(ServerLease.verify(first,leaf,root,issuer,epoch,current,live).active());
        denied(()->ServerLease.verify(first,certificate(directory.resolve("missing-san-cert.pem")),root,issuer,epoch,current,live));
        denied(()->ServerLease.verify(first,leaf,root,"0".repeat(64),epoch,current,live));denied(()->ServerLease.verify(first,leaf,root,issuer,UUID.randomUUID(),current,live));
        Map<String,Object> changed=WireJson.read(snapshot,65536);WireJson.object(WireJson.object(changed.get("grant")).get("scope")).put("visitorApp","org.example.other");AuthoritySnapshot other=AuthoritySnapshot.parse(changed);denied(()->ServerLease.verify(first,leaf,root,issuer,epoch,other,live));
        Map<String,Object> invalid=WireJson.read(Files.readAllBytes(directory.resolve("invalid.json")),65536);for(Object token:invalid.values())denied(()->ServerLease.verify((String)token,leaf,root,issuer,epoch,current,live));
        stage="clock-start";FakeClock fake=new FakeClock(issued);LeaseClock clock=new LeaseClock(fake);ServerLease.Window window=new ServerLease.Window();ServerLease.Receipt original=ServerLease.verify(first,leaf,root,issuer,epoch,current,clock);check(window.accept(original)==original);fake.advance(60000000000L);stage="clock-replay";ServerLease.Receipt replay=ServerLease.verify(first,leaf,root,issuer,epoch,current,clock);check(window.accept(replay)==original);stage="clock-next";window.accept(ServerLease.verify(next,leaf,root,issuer,epoch,current,clock));fake.advance(61000000000L);ServerLease.Window expired=window;LeaseClock expiredClock=clock;denied(expired::current);denied(()->ServerLease.verify(next,leaf,root,issuer,epoch,current,expiredClock));
        stage="clock-reset";fake=new FakeClock(issued);clock=new LeaseClock(fake);window=new ServerLease.Window();original=ServerLease.verify(first,leaf,root,issuer,epoch,current,clock);window.accept(original);fake.advance(10000000000L);check(window.current().active());fake.advance(-1000000000L);ServerLease.Window rolled=window;denied(rolled::current);fake.advance(2000000000L);denied(rolled::current);
        stage="clock-reset";fake=new FakeClock(issued);clock=new LeaseClock(fake);window=new ServerLease.Window();original=ServerLease.verify(first,leaf,root,issuer,epoch,current,clock);window.accept(original);window.close();ServerLease.Window closed=window;ServerLease.Receipt prior=original;denied(()->closed.accept(prior));
        System.out.println(new String(WireJson.write(WireJson.map("passed",true,"checks",checks,"goIssuedEs256",true,"signedMalformedRejected",true,"privateKeyExported",false),65536),java.nio.charset.StandardCharsets.UTF_8));
    }
    private static final class FakeClock implements LeaseClock.Source {
        private final long utc;private long nano;FakeClock(long utc){this.utc=utc;}public long utcMillis(){return utc;}public long nanos(){return nano;}void advance(long elapsed){nano+=elapsed;}
    }
}
