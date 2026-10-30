package dev.linkgate.transport;

import java.io.StringWriter;
import java.net.Socket;
import java.security.GeneralSecurityException;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.Principal;
import java.security.PrivateKey;
import java.security.PublicKey;
import java.security.SecureRandom;
import java.security.cert.Certificate;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.security.interfaces.ECPublicKey;
import java.security.spec.ECGenParameterSpec;
import java.security.spec.ECParameterSpec;
import java.util.Arrays;
import java.util.List;
import javax.net.ssl.KeyManager;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLEngine;
import javax.net.ssl.SSLParameters;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.TrustManagerFactory;
import javax.net.ssl.X509ExtendedKeyManager;
import javax.net.ssl.X509ExtendedTrustManager;
import javax.net.ssl.X509TrustManager;
import org.bouncycastle.asn1.x500.X500Name;
import org.bouncycastle.openssl.jcajce.JcaPEMWriter;
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder;
import org.bouncycastle.pkcs.jcajce.JcaPKCS10CertificationRequestBuilder;

public final class DeviceIdentity {
    private final PrivateKey key;private final X509Certificate certificate,root;
    public DeviceIdentity(PrivateKey key,X509Certificate certificate,X509Certificate root)throws GeneralSecurityException {
        this.key=key;this.certificate=certificate;this.root=root;
        validateDevice(certificate,root);
        // Prove key ownership without exporting a Keystore key.
        byte[] challenge=new byte[32];new SecureRandom().nextBytes(challenge);
        java.security.Signature signer=java.security.Signature.getInstance("SHA256withECDSA");signer.initSign(key);signer.update(challenge);byte[] signature=signer.sign();
        signer.initVerify(certificate.getPublicKey());signer.update(challenge);if(!signer.verify(signature))throw new GeneralSecurityException("Device key mismatch.");
    }
    public String deviceId()throws GeneralSecurityException{return keyId(certificate.getPublicKey());}
    public long expiresUtcMs(){return certificate.getNotAfter().getTime();}
    public static String keyId(PublicKey key)throws GeneralSecurityException {
        byte[] digest=MessageDigest.getInstance("SHA-256").digest(key.getEncoded());StringBuilder output=new StringBuilder(64);
        for(byte value:digest){output.append(Character.forDigit((value>>>4)&15,16));output.append(Character.forDigit(value&15,16));}return output.toString();
    }
    public static String request(PrivateKey key,PublicKey publicKey)throws GeneralSecurityException {
        requireP256(publicKey);
        try {
            var request=new JcaPKCS10CertificationRequestBuilder(new X500Name("CN=LinkGate device"),publicKey).build(new JcaContentSignerBuilder("SHA256withECDSA").build(key));
            StringWriter text=new StringWriter();try(JcaPEMWriter writer=new JcaPEMWriter(text)){writer.writeObject(request);}return text.toString();
        }catch(Exception e){throw new GeneralSecurityException("Device request failed.");}
    }
    public static void validateDevice(X509Certificate certificate,X509Certificate root)throws GeneralSecurityException {
        validateDevice(certificate,root,new java.util.Date());
    }
    public static void validateDevice(X509Certificate certificate,X509Certificate root,java.util.Date now)throws GeneralSecurityException {
        java.util.Date checked=new java.util.Date(now.getTime());certificate.checkValidity(checked);root.checkValidity(checked);requireP256(certificate.getPublicKey());
        if(certificate.getBasicConstraints()!=-1||root.getBasicConstraints()<0)throw new CertificateException("Invalid device certificate profile.");
        List<String> usages=certificate.getExtendedKeyUsage();
        if(usages==null||!usages.containsAll(Arrays.asList("1.3.6.1.5.5.7.3.1","1.3.6.1.5.5.7.3.2")))throw new CertificateException("Invalid device certificate profile.");
        java.security.cert.CertPath path=java.security.cert.CertificateFactory.getInstance("X.509").generateCertPath(Arrays.asList(certificate));
        var parameters=new java.security.cert.PKIXParameters(java.util.Collections.singleton(new java.security.cert.TrustAnchor(root,null)));parameters.setRevocationEnabled(false);parameters.setDate(checked);
        java.security.cert.CertPathValidator.getInstance("PKIX").validate(path,parameters);
        boolean name=false;var names=certificate.getSubjectAlternativeNames();
        if(names!=null)for(List<?> entry:names)if(entry.size()==2&&Integer.valueOf(2).equals(entry.get(0))&&"linkgate.device".equals(entry.get(1)))name=true;
        if(!name)throw new CertificateException("Invalid device certificate name.");
    }
    private static void requireP256(PublicKey key)throws GeneralSecurityException {
        if(!(key instanceof ECPublicKey))throw new GeneralSecurityException("P-256 key required.");
        var algorithm=java.security.AlgorithmParameters.getInstance("EC");algorithm.init(new ECGenParameterSpec("secp256r1"));ECParameterSpec expected=algorithm.getParameterSpec(ECParameterSpec.class),actual=((ECPublicKey)key).getParams();
        if(!expected.getCurve().equals(actual.getCurve())||!expected.getGenerator().equals(actual.getGenerator())||!expected.getOrder().equals(actual.getOrder())||expected.getCofactor()!=actual.getCofactor())throw new GeneralSecurityException("P-256 key required.");
    }
    public SSLSocket connect(Socket socket,int port,String expectedPeer,int timeoutMs)throws GeneralSecurityException,java.io.IOException {
        if(expectedPeer==null||!expectedPeer.matches("[0-9a-f]{64}"))throw new GeneralSecurityException("Explicit peer key required.");
        ClientTls client=tls(expectedPeer);
        SSLSocket secure=(SSLSocket)client.socketFactory.createSocket(socket,"linkgate.device",port,true);
        try {
            secure.setUseClientMode(true);secure.setSoTimeout(timeoutMs);SSLParameters parameters=secure.getSSLParameters();parameters.setEndpointIdentificationAlgorithm("HTTPS");
            List<String> supported=Arrays.asList(secure.getSupportedProtocols());parameters.setProtocols(supported.contains("TLSv1.3")?new String[]{"TLSv1.3","TLSv1.2"}:new String[]{"TLSv1.2"});secure.setSSLParameters(parameters);secure.startHandshake();
            X509Certificate peer=(X509Certificate)secure.getSession().getPeerCertificates()[0];validateDevice(peer,root);
            if(!expectedPeer.equals(keyId(peer.getPublicKey())))throw new CertificateException("Peer key mismatch.");return secure;
        }catch(GeneralSecurityException|java.io.IOException|RuntimeException e){secure.close();throw e;}
    }
    public static final class ClientTls {
        public final javax.net.ssl.SSLSocketFactory socketFactory;
        public final X509TrustManager trustManager;
        private ClientTls(javax.net.ssl.SSLSocketFactory factory,X509TrustManager trust){socketFactory=factory;trustManager=trust;}
    }
    public ClientTls tls(String expectedPeer)throws GeneralSecurityException {
        if(expectedPeer==null||!expectedPeer.matches("[0-9a-f]{64}"))throw new GeneralSecurityException("Explicit peer key required.");
        certificate.checkValidity();root.checkValidity();
        KeyStore trust=KeyStore.getInstance(KeyStore.getDefaultType());try{trust.load(null);}catch(java.io.IOException e){throw new GeneralSecurityException("Trust store unavailable.");}trust.setCertificateEntry("owner",root);
        TrustManagerFactory factory=TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());factory.init(trust);
        X509ExtendedTrustManager manager=null;
        for(TrustManager candidate:factory.getTrustManagers())if(candidate instanceof X509ExtendedTrustManager)manager=(X509ExtendedTrustManager)candidate;
        if(manager==null)throw new GeneralSecurityException("Extended platform certificate validation unavailable.");
        var pinned=new PinnedTrust(manager,expectedPeer,root);
        SSLContext context=SSLContext.getInstance("TLS");context.init(new KeyManager[]{new DeviceKeyManager()},new TrustManager[]{pinned},new SecureRandom());
        return new ClientTls(context.getSocketFactory(),pinned);
    }
    private final class DeviceKeyManager extends X509ExtendedKeyManager {
        @Override public String[] getClientAliases(String type,Principal[] issuers){return "EC".equals(type)?new String[]{"device"}:null;}
        @Override public String chooseClientAlias(String[] types,Principal[] issuers,Socket socket){return Arrays.asList(types).contains("EC")?"device":null;}
        @Override public String chooseEngineClientAlias(String[] types,Principal[] issuers,SSLEngine engine){return chooseClientAlias(types,issuers,null);}
        @Override public String[] getServerAliases(String type,Principal[] issuers){return null;}
        @Override public String chooseServerAlias(String type,Principal[] issuers,Socket socket){return null;}
        @Override public PrivateKey getPrivateKey(String alias){return "device".equals(alias)?key:null;}
        @Override public X509Certificate[] getCertificateChain(String alias){return "device".equals(alias)?new X509Certificate[]{certificate,root}:null;}
    }
    private static final class PinnedTrust extends X509ExtendedTrustManager {
        private final X509ExtendedTrustManager delegate;private final String expected;private final X509Certificate root;
        PinnedTrust(X509ExtendedTrustManager delegate,String expected,X509Certificate root){this.delegate=delegate;this.expected=expected;this.root=root;}
        private void pin(X509Certificate[] chain)throws CertificateException {
            try{if(chain==null||chain.length==0||!expected.equals(keyId(chain[0].getPublicKey())))throw new CertificateException("Peer key mismatch.");validateDevice(chain[0],root);}
            catch(GeneralSecurityException e){throw new CertificateException("Peer key mismatch.");}
        }
        @Override public void checkServerTrusted(X509Certificate[] chain,String auth,Socket socket)throws CertificateException{delegate.checkServerTrusted(chain,auth,socket);pin(chain);}
        @Override public void checkServerTrusted(X509Certificate[] chain,String auth,SSLEngine engine)throws CertificateException{delegate.checkServerTrusted(chain,auth,engine);pin(chain);}
        @Override public void checkServerTrusted(X509Certificate[] chain,String auth)throws CertificateException{delegate.checkServerTrusted(chain,auth);pin(chain);}
        @Override public void checkClientTrusted(X509Certificate[] chain,String auth,Socket socket)throws CertificateException{throw new CertificateException("Client role required.");}
        @Override public void checkClientTrusted(X509Certificate[] chain,String auth,SSLEngine engine)throws CertificateException{throw new CertificateException("Client role required.");}
        @Override public void checkClientTrusted(X509Certificate[] chain,String auth)throws CertificateException{throw new CertificateException("Client role required.");}
        @Override public X509Certificate[] getAcceptedIssuers(){return delegate.getAcceptedIssuers();}
    }
}
