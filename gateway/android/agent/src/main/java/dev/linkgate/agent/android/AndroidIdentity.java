package dev.linkgate.agent.android;

import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import dev.linkgate.agent.AgentRegistry;
import dev.linkgate.transport.DeviceIdentity;
import java.io.ByteArrayInputStream;
import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.security.KeyPairGenerator;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.PrivateKey;
import java.security.cert.Certificate;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.security.spec.ECGenParameterSpec;
import java.util.Base64;
import java.util.Date;
import javax.security.auth.x500.X500Principal;

final class AndroidIdentity {
    private static final String ALIAS="LinkGate.device.v1";
    private final AgentRegistry registry;
    AndroidIdentity(AgentRegistry registry){this.registry=registry;}
    private KeyStore store()throws Exception{KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);return store;}
    synchronized String request()throws Exception {
        KeyStore store=store();
        String bound=registry.boundDeviceId();
        if(bound!=null&&(!store.containsAlias(ALIAS)||!bound.equals(DeviceIdentity.keyId(store.getCertificate(ALIAS).getPublicKey()))))throw new java.security.GeneralSecurityException("Existing device identity unavailable.");
        if(!store.containsAlias(ALIAS)) {
            KeyPairGenerator generator=KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC,"AndroidKeyStore");
            generator.initialize(new KeyGenParameterSpec.Builder(ALIAS,KeyProperties.PURPOSE_SIGN|KeyProperties.PURPOSE_VERIFY).setAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1")).setDigests(KeyProperties.DIGEST_SHA256,KeyProperties.DIGEST_NONE).setUserAuthenticationRequired(false).setCertificateSubject(new X500Principal("CN=LinkGate device request")).setCertificateSerialNumber(BigInteger.ONE).setCertificateNotBefore(new Date(System.currentTimeMillis()-300000)).setCertificateNotAfter(new Date(System.currentTimeMillis()+31536000000L)).build());generator.generateKeyPair();
        }
        proveTlsKey((PrivateKey)store.getKey(ALIAS,null),store.getCertificate(ALIAS));
        return DeviceIdentity.request((PrivateKey)store.getKey(ALIAS,null),store.getCertificate(ALIAS).getPublicKey());
    }
    synchronized DeviceIdentity load()throws Exception {
        KeyStore store=store();Certificate[] chain=store.getCertificateChain(ALIAS);
        if(chain==null||chain.length!=2||!(chain[0] instanceof X509Certificate)||!(chain[1] instanceof X509Certificate))throw new java.security.GeneralSecurityException("Device enrollment required.");
        PrivateKey key=(PrivateKey)store.getKey(ALIAS,null);proveTlsKey(key,chain[0]);return new DeviceIdentity(key,(X509Certificate)chain[0],(X509Certificate)chain[1]);
    }
    synchronized void install(String leafPem,String rootPem)throws Exception {
        X509Certificate leaf=parse(leafPem),root=parse(rootPem);KeyStore store=store();if(!store.containsAlias(ALIAS))throw new java.security.GeneralSecurityException("Original device request required.");
        PrivateKey key=(PrivateKey)store.getKey(ALIAS,null);proveTlsKey(key,leaf);DeviceIdentity identity=new DeviceIdentity(key,leaf,root);
        registry.bindIdentity(identity.deviceId(),fingerprint(root));store.setKeyEntry(ALIAS,key,null,new Certificate[]{leaf,root});
    }
    synchronized String deviceId()throws Exception{Certificate certificate=store().getCertificate(ALIAS);return certificate==null?null:DeviceIdentity.keyId(certificate.getPublicKey());}
    synchronized X509Certificate root()throws Exception{Certificate[] chain=store().getCertificateChain(ALIAS);if(chain==null||chain.length!=2)throw new java.security.GeneralSecurityException("Device enrollment required.");return (X509Certificate)chain[1];}
    synchronized String peerId(String certificatePem)throws Exception {X509Certificate peer=parse(certificatePem);DeviceIdentity.validateDevice(peer,root());return DeviceIdentity.keyId(peer.getPublicKey());}
    synchronized String certificatePem()throws Exception {Certificate[] chain=store().getCertificateChain(ALIAS);if(chain==null||chain.length!=2)throw new java.security.GeneralSecurityException("Device enrollment required.");return pem((X509Certificate)chain[0]);}
    static X509Certificate parse(String pem)throws Exception {
        if(pem==null||pem.getBytes(StandardCharsets.UTF_8).length>65536)throw new IllegalArgumentException("Bounded public certificate required.");
        return (X509Certificate)CertificateFactory.getInstance("X.509").generateCertificate(new ByteArrayInputStream(pem.getBytes(StandardCharsets.UTF_8)));
    }
    static String fingerprint(X509Certificate certificate)throws Exception {
        StringBuilder text=new StringBuilder();for(byte value:MessageDigest.getInstance("SHA-256").digest(certificate.getEncoded())){text.append(Character.forDigit((value>>>4)&15,16));text.append(Character.forDigit(value&15,16));}return text.toString();
    }
    static String pem(X509Certificate certificate)throws Exception{return "-----BEGIN CERTIFICATE-----\n"+Base64.getMimeEncoder(64,new byte[]{10}).encodeToString(certificate.getEncoded())+"\n-----END CERTIFICATE-----\n";}
    private static void proveTlsKey(PrivateKey key,Certificate certificate)throws Exception {
        byte[] digest=new byte[32];new java.security.SecureRandom().nextBytes(digest);var signature=java.security.Signature.getInstance("NONEwithECDSA");signature.initSign(key);signature.update(digest);byte[] signed=signature.sign();signature.initVerify(certificate.getPublicKey());signature.update(digest);if(!signature.verify(signed))throw new java.security.GeneralSecurityException("TLS device key mismatch.");
    }
}
