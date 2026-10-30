package dev.linkgate.authority;

import com.nimbusds.jose.crypto.ECDSAVerifier;
import com.nimbusds.jose.proc.SecurityContext;
import com.nimbusds.jwt.JWTClaimsSet;
import com.nimbusds.jwt.SignedJWT;
import com.nimbusds.jwt.proc.DefaultJWTClaimsVerifier;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.transport.DeviceIdentity;
import java.math.BigInteger;
import java.security.cert.X509Certificate;
import java.security.interfaces.ECPublicKey;
import java.util.Arrays;
import java.util.Base64;
import java.util.Collections;
import java.util.Date;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.UUID;

public final class ServerLease {
    private ServerLease(){}
    public static final class Receipt {
        public final UUID id,serverEpoch;public final String issuer;public final BigInteger sequence;public final AuthoritySnapshot authority;
        private final LeaseClock clock;private final long began,lifetimeNanos,expiresMillis;private long previousElapsed;private boolean invalid;
        private Receipt(UUID id,String issuer,UUID epoch,BigInteger sequence,AuthoritySnapshot current,LeaseClock clock,long began,long lifetime,long expires){this.id=id;this.issuer=issuer;serverEpoch=epoch;this.sequence=sequence;authority=current;this.clock=clock;this.began=began;lifetimeNanos=lifetime;expiresMillis=expires;}
        public long remainingMillis(){if(!active())return 0;try{LeaseClock.Reading now=clock.read();return Math.min((lifetimeNanos-Math.subtractExact(now.nanos,began))/1000000L,Math.subtractExact(expiresMillis,now.utcMillis));}catch(Exception failure){return 0;}}
        boolean hasClock(LeaseClock expected){return clock==expected;}
        public synchronized boolean active(){if(invalid)return false;try{LeaseClock.Reading now=clock.read();long elapsed=Math.subtractExact(now.nanos,began);if(elapsed<0||elapsed<previousElapsed||elapsed>=lifetimeNanos||now.utcMillis>=expiresMillis){invalid=true;return false;}previousElapsed=elapsed;return true;}catch(LeaseRejectedException|RuntimeException failure){invalid=true;return false;}}
    }
    public static Receipt verify(String token,X509Certificate issuerCertificate,X509Certificate ownerRoot,String expectedIssuer,UUID expectedEpoch,AuthoritySnapshot current,LeaseClock clock)throws LeaseRejectedException {
        LeaseRejectedException.Stage stage=LeaseRejectedException.Stage.INPUT;
        try {
            if(token==null||token.isEmpty()||token.length()>16384||current==null||expectedEpoch==null||clock==null)throw new IllegalArgumentException();LeaseClock.Reading reading=clock.read();long now=reading.utcMillis;stage=LeaseRejectedException.Stage.CERTIFICATE;DeviceIdentity.validateDevice(issuerCertificate,ownerRoot,new Date(now));String issuer=DeviceIdentity.keyId(issuerCertificate.getPublicKey());if(!issuer.equals(WireJson.key(expectedIssuer)))throw new IllegalArgumentException();
            stage=LeaseRejectedException.Stage.COMPACT;String[] parts=token.split("\\.",-1);if(parts.length!=3||parts[0].length()>2048||parts[2].length()!=86)throw new IllegalArgumentException();byte[][] bytes=new byte[3][];for(int index=0;index<3;index++){String part=parts[index];if(!part.matches("[A-Za-z0-9_-]+"))throw new IllegalArgumentException();bytes[index]=Base64.getUrlDecoder().decode(part);if(!Base64.getUrlEncoder().withoutPadding().encodeToString(bytes[index]).equals(part))throw new IllegalArgumentException();}if(bytes[2].length!=64)throw new IllegalArgumentException();
            stage=LeaseRejectedException.Stage.HEADER;Map<String,Object> header=WireJson.read(bytes[0],2048);WireJson.fields(header,"alg","typ","kid");if(!"ES256".equals(header.get("alg"))||!"linkgate-lease+jwt".equals(header.get("typ"))||!issuer.equals(header.get("kid")))throw new IllegalArgumentException();
            stage=LeaseRejectedException.Stage.CLAIMS;Map<String,Object> claims=WireJson.read(bytes[1],16384);WireJson.fields(claims,"iss","sub","aud","jti","iat","nbf","exp","v","serverEpoch","sequence","scope","visitorRevision","publisherRevision","serviceRevision","grantRevision","protocol","authority");
            stage=LeaseRejectedException.Stage.AUTHORITY;Object rawAudience=claims.get("aud");if(!(rawAudience instanceof List<?>))throw new IllegalArgumentException();List<?> audience=(List<?>)rawAudience;
            if(audience.size()!=1||!current.scope.target.publisherDevice.equals(audience.get(0))||!issuer.equals(claims.get("iss"))||!current.scope.visitorDevice.equals(claims.get("sub"))||WireJson.integer(claims.get("v"),1,1)!=1||!expectedEpoch.equals(AuthoritySnapshot.uuid(claims.get("serverEpoch")))||!current.scope.equals(AuthoritySnapshot.scope(claims.get("scope"))))throw new IllegalArgumentException();
            stage=LeaseRejectedException.Stage.SCOPE;if(!current.visitorRevision.equals(AuthoritySnapshot.counter(claims.get("visitorRevision"),true))||!current.publisherRevision.equals(AuthoritySnapshot.counter(claims.get("publisherRevision"),true))||!current.serviceRevision.equals(AuthoritySnapshot.counter(claims.get("serviceRevision"),true))||!current.grantRevision.equals(AuthoritySnapshot.counter(claims.get("grantRevision"),true))||!current.protocol.equals(claims.get("protocol"))||!current.authority.equals(claims.get("authority")))throw new IllegalArgumentException();
            stage=LeaseRejectedException.Stage.TIME;UUID id=AuthoritySnapshot.uuid(claims.get("jti"));BigInteger sequence=AuthoritySnapshot.counter(claims.get("sequence"),false);long issued=WireJson.integer(claims.get("iat"),1,Long.MAX_VALUE),notBefore=WireJson.integer(claims.get("nbf"),1,Long.MAX_VALUE),expires=WireJson.integer(claims.get("exp"),1,Long.MAX_VALUE);if(issued>now/1000L||notBefore!=issued||expires<=issued||expires-issued>120||expires<=now/1000L)throw new IllegalArgumentException();
            stage=LeaseRejectedException.Stage.SIGNATURE;SignedJWT signed=SignedJWT.parse(token);if(!signed.verify(new ECDSAVerifier((ECPublicKey)issuerCertificate.getPublicKey())))throw new IllegalArgumentException();
            // Nimbus uses a strict Date.before comparison for nbf at zero skew.
            // Exact inclusive nbf and exclusive exp checks above retain zero effective
            // leeway; one library second permits the exact nbf boundary to reach them.
            stage=LeaseRejectedException.Stage.STANDARD_CLAIMS;DefaultJWTClaimsVerifier<SecurityContext> verifier=new DefaultJWTClaimsVerifier<SecurityContext>(Collections.singleton(current.scope.target.publisherDevice),new JWTClaimsSet.Builder().issuer(issuer).subject(current.scope.visitorDevice).build(),new HashSet<>(Arrays.asList("iss","sub","aud","jti","iat","nbf","exp")),null){@Override protected Date currentTime(){return new Date(now);}};verifier.setMaxClockSkew(1);verifier.verify(signed.getJWTClaimsSet(),null);
            stage=LeaseRejectedException.Stage.RECEIPT;long deadline=Math.min(Math.multiplyExact(expires,1000L),Math.min(issuerCertificate.getNotAfter().getTime(),ownerRoot.getNotAfter().getTime())),remaining=Math.subtractExact(deadline,now);if(remaining<=0||remaining>120000)throw new IllegalArgumentException();Receipt receipt=new Receipt(id,issuer,expectedEpoch,sequence,current,clock,reading.nanos,Math.multiplyExact(remaining,1000000L),deadline);if(!receipt.active())throw new IllegalArgumentException();return receipt;
        }catch(LeaseRejectedException failure){throw failure;}
        catch(Exception failure){throw new LeaseRejectedException(stage);}
    }
    public static final class Window {
        private Receipt current;private LeaseClock clock;private boolean closed;
        private void observe(Receipt value)throws LeaseRejectedException {if(clock!=null&&clock!=value.clock){closed=true;throw new LeaseRejectedException();}clock=value.clock;try{clock.read();}catch(LeaseRejectedException failure){closed=true;throw failure;}}
        public synchronized Receipt accept(Receipt receipt)throws LeaseRejectedException {
            if(closed||receipt==null)throw new LeaseRejectedException();observe(receipt);if(!receipt.active())throw new LeaseRejectedException();
            if(current!=null){if(!current.authority.scope.equals(receipt.authority.scope)||!current.issuer.equals(receipt.issuer)||!current.serverEpoch.equals(receipt.serverEpoch))throw new LeaseRejectedException();if(current.id.equals(receipt.id)){if(!current.sequence.equals(receipt.sequence)||!current.active())throw new LeaseRejectedException();return current;}if(receipt.sequence.compareTo(current.sequence)<=0)throw new LeaseRejectedException();}current=receipt;return receipt;
        }
        public synchronized Receipt current()throws LeaseRejectedException {if(closed||current==null)throw new LeaseRejectedException();observe(current);if(!current.active())throw new LeaseRejectedException();return current;}
        public synchronized void close(){closed=true;}
    }
}
