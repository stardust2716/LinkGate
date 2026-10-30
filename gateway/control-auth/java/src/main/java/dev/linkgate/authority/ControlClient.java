package dev.linkgate.authority;

import dev.linkgate.contracts.WireJson;
import dev.linkgate.transport.DeviceIdentity;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.InetAddress;
import java.net.Proxy;
import java.security.GeneralSecurityException;
import java.security.cert.X509Certificate;
import java.util.Arrays;
import java.util.Collections;
import java.util.Date;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import okhttp3.Call;
import okhttp3.ConnectionPool;
import okhttp3.ConnectionSpec;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Protocol;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;
import okhttp3.ResponseBody;
import okhttp3.TlsVersion;

/** Agent-only current authority client. No business SDK or owner mutation API. */
public final class ControlClient implements AutoCloseable {
    public static final class Refused extends IOException {
        public final String code;
        private Refused(String code) { super("Server control request refused."); this.code = code; }
    }
    public static final class Authorized {
        public final String token;
        public final ServerLease.Receipt receipt;
        private Authorized(String token, ServerLease.Receipt receipt) { this.token = token; this.receipt = receipt; }
        @Override public String toString() { return "Verified server authority"; }
    }
    private static final Set<String> CODES = Collections.unmodifiableSet(new HashSet<>(Arrays.asList(
        "AUTH_REQUIRED", "IDENTITY_CHANGED", "INVALID_REQUEST", "VERSION_UNSUPPORTED", "METHOD_UNSUPPORTED",
        "REVIEW_STALE", "OPERATION_CONFLICT", "OPERATION_UNKNOWN", "RESOURCE_EXHAUSTED", "STORAGE_UNAVAILABLE", "CLOCK_UNTRUSTED")));
    private final OkHttpClient http;
    private final String endpoint, issuer, device;
    private final X509Certificate server, root;
    private final LeaseClock clock;
    private final Semaphore capacity = new Semaphore(8);
    private final Set<Call> active = ConcurrentHashMap.newKeySet();
    private UUID epoch;
    private boolean closed;

    private ControlClient(byte[] ipv4, int port, DeviceIdentity identity, X509Certificate server, X509Certificate root, LeaseClock clock) throws GeneralSecurityException, IOException {
        if (ipv4 == null || ipv4.length != 4 || port < 1 || port > 65535 || clock == null) throw new LeaseRejectedException();
        byte[] fixed = ipv4.clone(); this.server = server; this.root = root; this.clock = clock;
        DeviceIdentity.validateDevice(server, root, new java.util.Date(clock.read().utcMillis));
        issuer = DeviceIdentity.keyId(server.getPublicKey()); device = identity.deviceId();
        DeviceIdentity.ClientTls tls = identity.tls(issuer);
        endpoint = "https://linkgate.device:" + port + "/control/v1";
        http = new OkHttpClient.Builder()
            .proxy(Proxy.NO_PROXY)
            .dns(host -> { if (!"linkgate.device".equals(host)) throw new java.net.UnknownHostException("Control endpoint rejected."); return Collections.singletonList(InetAddress.getByAddress(fixed)); })
            .sslSocketFactory(tls.socketFactory, tls.trustManager)
            .connectionSpecs(Collections.singletonList(new ConnectionSpec.Builder(ConnectionSpec.MODERN_TLS).tlsVersions(TlsVersion.TLS_1_3, TlsVersion.TLS_1_2).build()))
            .protocols(Collections.singletonList(Protocol.HTTP_1_1))
            .connectionPool(new ConnectionPool(0, 1, TimeUnit.SECONDS))
            .retryOnConnectionFailure(false).followRedirects(false).followSslRedirects(false)
            .callTimeout(3, TimeUnit.SECONDS).connectTimeout(2, TimeUnit.SECONDS)
            .readTimeout(3, TimeUnit.SECONDS).writeTimeout(3, TimeUnit.SECONDS).build();
    }

    public static ControlClient connect(byte[] ipv4, int port, DeviceIdentity identity, X509Certificate server, X509Certificate root, LeaseClock clock) throws GeneralSecurityException, IOException {
        ControlClient client = new ControlClient(ipv4, port, identity, server, root, clock);
        try {
            Map<String,Object> result = client.call("hello", Map.of(), false);
            WireJson.fields(result, "server", "epoch", "maxLeaseSeconds");
            if (!client.issuer.equals(result.get("server")) || WireJson.integer(result.get("maxLeaseSeconds"), 120, 120) != 120) throw new LeaseRejectedException();
            synchronized (client) { client.checkOpen(); client.epoch = AuthoritySnapshot.uuid(result.get("epoch")); }
            return client;
        } catch (IOException | GeneralSecurityException | RuntimeException failure) { client.close(); throw failure; }
    }

    public Authorized acquire(String application, AuthoritySnapshot.Target target) throws IOException, LeaseRejectedException {
        AuthoritySnapshot.Scope expected = new AuthoritySnapshot.Scope(device, application, target);
        Map<String,Object> value = call("lease.acquire", Map.of("visitorApp", application, "target", target(target)), true);
        try {
            WireJson.fields(value, "current", "token", "serverEpoch", "issuer");
            return verify(WireJson.text(value.get("token"), 16384), value, expected);
        } catch (RuntimeException failure) { throw new LeaseRejectedException(); }
    }

    public Authorized validate(String token, AuthoritySnapshot.Scope expected) throws IOException, LeaseRejectedException {
        if (expected == null || !expected.target.publisherDevice.equals(device) || token == null || token.isEmpty() || token.length() > 16384) throw new LeaseRejectedException();
        Map<String,Object> value = call("lease.validate", Map.of("token", token), true);
        try { WireJson.fields(value, "current", "serverEpoch", "issuer"); return verify(token, value, expected); }
        catch (RuntimeException failure) { throw new LeaseRejectedException(); }
    }

    public boolean[] check(java.util.List<ServerLease.Receipt> receipts) throws IOException, LeaseRejectedException {
        if(receipts==null||receipts.isEmpty()||receipts.size()>64)throw new LeaseRejectedException();
        java.util.List<ServerLease.Receipt> fixed=new java.util.ArrayList<>(receipts);
        if(fixed.isEmpty()||fixed.size()>64)throw new LeaseRejectedException();
        java.util.List<Object> wanted=new java.util.ArrayList<>();
        for(ServerLease.Receipt receipt:fixed) {
            synchronized(this){checkOpen();if(receipt==null||!receipt.hasClock(clock)||!receipt.issuer.equals(issuer)||!receipt.serverEpoch.equals(epoch)||(!receipt.authority.scope.visitorDevice.equals(device)&&!receipt.authority.scope.target.publisherDevice.equals(device))||!receipt.active())throw new LeaseRejectedException();}
            AuthoritySnapshot value=receipt.authority;
            wanted.add(Map.of("scope",Map.of("visitorDevice",value.scope.visitorDevice,"visitorApp",value.scope.visitorApp,"target",target(value.scope.target)),"visitorRevision",value.visitorRevision.toString(),"publisherRevision",value.publisherRevision.toString(),"serviceRevision",value.serviceRevision.toString(),"grantRevision",value.grantRevision.toString(),"protocol",value.protocol,"authority",value.authority));
        }
        Map<String,Object> result=call("lease.check",Map.of("leases",wanted),true);
        try {
            WireJson.fields(result,"valid","serverEpoch","issuer");
            boolean changed;synchronized(this){checkOpen();changed=!epoch.equals(AuthoritySnapshot.uuid(result.get("serverEpoch")))||!issuer.equals(result.get("issuer"));}
            if(changed){close();throw new LeaseRejectedException();}
            if(!(result.get("valid") instanceof java.util.List<?>))throw new IllegalArgumentException();
            java.util.List<?> raw=(java.util.List<?>)result.get("valid");if(raw.size()!=fixed.size())throw new IllegalArgumentException();boolean[] valid=new boolean[raw.size()];
            for(int i=0;i<valid.length;i++){if(!(raw.get(i) instanceof Boolean))throw new IllegalArgumentException();valid[i]=(Boolean)raw.get(i)&&fixed.get(i).active();}return valid;
        }catch(RuntimeException failure){throw new LeaseRejectedException();}
    }

    private Authorized verify(String token, Map<String,Object> result, AuthoritySnapshot.Scope expected) throws LeaseRejectedException {
        UUID known; synchronized (this) { checkOpen(); known = epoch; }
        if (!known.equals(AuthoritySnapshot.uuid(result.get("serverEpoch"))) || !issuer.equals(result.get("issuer"))) { close(); throw new LeaseRejectedException(); }
        AuthoritySnapshot snapshot = AuthoritySnapshot.parse(WireJson.object(result.get("current")));
        if (!snapshot.scope.equals(expected)) throw new LeaseRejectedException();
        ServerLease.Receipt receipt = ServerLease.verify(token, server, root, issuer, known, snapshot, clock);
        synchronized (this) { checkOpen(); return new Authorized(token, receipt); }
    }

    private Map<String,Object> call(String method, Map<String,Object> parameters, boolean connected) throws IOException, LeaseRejectedException {
        synchronized (this) { checkOpen(); if (connected && epoch == null) throw new LeaseRejectedException(); }
        if (!capacity.tryAcquire()) throw new Refused("RESOURCE_EXHAUSTED");
        Call call = null;
        try {
            String id = UUID.randomUUID().toString();
            byte[] bytes = WireJson.write(Map.of("kind", "request", "apiVersion", 1, "requestId", id, "method", method, "params", parameters), 65536);
            Request request = new Request.Builder().url(endpoint).header("Accept-Encoding", "identity")
                .post(RequestBody.create(bytes, MediaType.get("application/json"))).build();
            call = http.newCall(request);
            synchronized (this) { checkOpen(); active.add(call); }
            long requestBegan=clock.read().nanos;
            try (Response reply = call.execute()) {
                ResponseBody body = reply.body();
                if (reply.code() != 200 || body == null || body.contentType() == null || !"application".equals(body.contentType().type()) || !"json".equals(body.contentType().subtype()) || reply.header("Content-Encoding") != null || body.contentLength() > 65536) throw new LeaseRejectedException();
                ByteArrayOutputStream buffer = new ByteArrayOutputStream(); byte[] chunk = new byte[8192];
                try (InputStream input = body.byteStream()) {
                    while (true) { int count = input.read(chunk); if (count == -1) break; if (buffer.size() + count > 65536) throw new LeaseRejectedException(); buffer.write(chunk, 0, count); }
                }
                Map<String,Object> frame = WireJson.read(buffer.toByteArray(), 65536); boolean error = frame.containsKey("error");
                WireJson.fields(frame, "kind", "apiVersion", "requestId", error ? "error" : "result");
                if (!"response".equals(frame.get("kind")) || WireJson.integer(frame.get("apiVersion"), 1, 1) != 1 || !id.equals(frame.get("requestId"))) throw new LeaseRejectedException();
                synchronized (this) { checkOpen(); }
                if (error) {
                    Map<String,Object> failure = WireJson.object(frame.get("error")); WireJson.fields(failure, "code", "stage", "retryable");
                    String code = WireJson.text(failure.get("code"), 32);
                    if (!CODES.contains(code) || !"control".equals(failure.get("stage")) || !Boolean.FALSE.equals(failure.get("retryable"))) throw new LeaseRejectedException();
                    throw new Refused(code);
                }
                Map<String,Object> result=WireJson.object(frame.get("result"));
                Date observed=reply.headers().getDate("Date");
                synchronized(this){checkOpen();if(observed!=null)clock.observeAuthenticatedResponseDate(observed.getTime(),requestBegan);}
                return result;
            }
        } catch (Refused failure) { throw failure; }
        catch (IOException failure) { throw new Refused("CONTROL_UNAVAILABLE"); }
        catch (RuntimeException failure) { throw new LeaseRejectedException(); }
        finally { if (call != null) active.remove(call); capacity.release(); }
    }

    private static Map<String,Object> target(AuthoritySnapshot.Target target) { return Map.of("publisherDevice", target.publisherDevice, "publisherApp", target.publisherApp, "service", target.service); }
    private void checkOpen() throws LeaseRejectedException { if (closed) throw new LeaseRejectedException(); }
    @Override public void close() {
        synchronized (this) { if (closed) return; closed = true; }
        for (Call call : active) call.cancel();
        http.dispatcher().executorService().shutdown(); http.connectionPool().evictAll();
    }
}
