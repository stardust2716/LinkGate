package dev.linkgate.client;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.content.pm.Signature;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import dev.linkgate.broker.ILinkGateControl;
import dev.linkgate.contracts.ApplicationManifest;
import dev.linkgate.contracts.RegistrationReply;
import dev.linkgate.contracts.ControlReply;
import dev.linkgate.contracts.SecureReply;
import java.io.IOException;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collection;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeoutException;

/** Pending-registration development SDK. Pins must come from a trusted install/configuration flow. */
public final class BrokerClient implements AutoCloseable {
    private static final ClientRuntime RUNTIME = new ClientRuntime();
    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    private static final String ACTION = "dev.linkgate.BIND_CONTROL_V1";
    private final Object lock = new Object();
    private final Context context;
    private final ComponentName component;
    private final PeerPin pin;
    private final ClientRuntime.Lease lease;
    private final CompletableFuture<BrokerClient> ready = new CompletableFuture<>();
    private PeerPin.Snapshot baseline;
    private boolean closed, bindInProgress = true, bindingAttempted, bound, verified, unbindQueued;
    private IBinder binder;
    private ILinkGateControl remote;
    private final ControlSession session;
    private final IBinder.DeathRecipient death = this::close;

    private final ServiceConnection connection = new ServiceConnection() {
        @Override public void onServiceConnected(ComponentName name, IBinder value) {
            if (!component.equals(name) || value == null) { close(); return; }
            if (!RUNTIME.execute(() -> accept(value))) close();
        }
        @Override public void onServiceDisconnected(ComponentName name) { close(); }
        @Override public void onBindingDied(ComponentName name) { close(); }
        @Override public void onNullBinding(ComponentName name) { close(); }
    };

    private BrokerClient(Context context, ComponentName component, PeerPin pin, ClientRuntime.Lease lease) {
        this.context = context.getApplicationContext(); this.component = component; this.pin = pin; this.lease = lease;
        if (this.context == null) throw new IllegalArgumentException("Application context required.");
        session = new ControlSession(RUNTIME, new ControlSession.Transport() {
            @Override public void verify() throws Exception { checkPeer(); }
            @Override public byte[] request(byte[] input) throws Exception {
                ILinkGateControl target;
                synchronized (lock) { if (closed || !verified || !bound) throw new IOException("Broker connection unavailable."); target = remote; }
                return target.request(input);
            }
        }, this::close);
    }

    public static CompletableFuture<BrokerClient> bindAsync(Context context, ComponentName component, Collection<String> trustedCurrentSigners) {
        if (context == null || component == null) throw new IllegalArgumentException("Explicit Broker required.");
        PeerPin pin = new PeerPin(component.getPackageName(), component.getClassName(), trustedCurrentSigners);
        ClientRuntime.Lease lease = RUNTIME.acquire();
        if (lease == null) return failed(new IllegalStateException("RESOURCE_EXHAUSTED"));
        final BrokerClient client;
        try { client = new BrokerClient(context, component, pin, lease); }
        catch (RuntimeException e) { lease.close(); throw e; }
        ScheduledFuture<?> deadline = RUNTIME.after(2000, () -> client.ready.completeExceptionally(new TimeoutException("Broker binding timed out.")));
        client.ready.whenComplete((value, error) -> { deadline.cancel(false); if (error != null) client.close(); });
        if (!RUNTIME.execute(client::bind)) {
            synchronized (client.lock) { client.bindInProgress = false; }
            client.ready.completeExceptionally(new IllegalStateException("RESOURCE_EXHAUSTED"));
        }
        return client.ready;
    }

    private void bind() {
        boolean accepted = false;
        try {
            PeerPin.Snapshot snapshot = snapshot(); pin.verify(snapshot);
            synchronized (lock) { if (closed) return; baseline = snapshot; bindingAttempted = true; }
            accepted = context.bindService(new Intent(ACTION).setComponent(component), connection, Context.BIND_AUTO_CREATE);
            if (!accepted) ready.completeExceptionally(new IOException("Broker binding rejected."));
        } catch (Exception e) { ready.completeExceptionally(new SecurityException("Broker binding failed.")); }
        finally {
            boolean cleanup;
            synchronized (lock) { bindInProgress = false; bound = accepted; cleanup = closed; }
            if (cleanup) cleanup(); else completeReady();
        }
    }

    private void accept(IBinder value) {
        boolean linked = false;
        try {
            checkPeer();
            if (!"dev.linkgate.broker.ILinkGateControl".equals(value.getInterfaceDescriptor())) throw new SecurityException();
            value.linkToDeath(death, 0); linked = true;
            synchronized (lock) {
                if (closed) { unlink(value); return; }
                binder = value; remote = ILinkGateControl.Stub.asInterface(value); verified = true;
            }
            completeReady();
        } catch (Exception e) {
            if (linked) unlink(value);
            ready.completeExceptionally(new SecurityException("Broker identity mismatch."));
            close();
        }
    }

    private void completeReady() {
        boolean available;
        synchronized (lock) { available = !closed && bound && verified; }
        if (available) ready.complete(this);
    }

    public CompletableFuture<ControlReply.Hello> helloAsync() { return session.helloAsync(); }

    public OperationFuture<RegistrationReply.Pending> registerPendingAsync(ApplicationManifest manifest, UUID operationId, boolean allowExperimentalPending) {
        return session.registerPendingAsync(manifest, operationId, allowExperimentalPending);
    }

    public CompletableFuture<RegistrationReply.Operation> getOperationStatusAsync(UUID operationId) {
        return session.getOperationStatusAsync(operationId);
    }
    public OperationFuture<SecureReply.Connection> openSecureDirectAsync(SecureReply.Scope scope,UUID operation,boolean allowExperimental) {
        return session.openSecureAsync(scope,operation,allowExperimental);
    }
    public OperationFuture<SecureReply.Connection> openSecureRelayAsync(SecureReply.Scope scope,UUID operation,boolean allowExperimental) {return session.openRelayAsync(scope,operation,allowExperimental);}
    public CompletableFuture<SecureReply.Connection> getSecureConnectionStatusAsync(UUID operation) {return session.secureStatusAsync(operation,false);}
    public CompletableFuture<SecureReply.Connection> closeSecureConnectionAsync(UUID operation) {return session.secureStatusAsync(operation,true);}
    public OperationFuture<SecureReply.GrantRequest> requestAccessAsync(SecureReply.Scope scope,UUID operation,String reason,boolean allowExperimental) {
        return session.requestAccessAsync(scope,operation,reason,allowExperimental);
    }
    public OperationFuture<android.os.ParcelFileDescriptor> attachStreamAsync(UUID operation,IBinder lifetimeToken,boolean allowExperimental) {
        return attachDescriptorAsync(operation,operation,lifetimeToken,allowExperimental,false);
    }
    public OperationFuture<dev.linkgate.contracts.ConnectionReply.Operation> openConnectionAsync(dev.linkgate.contracts.ConnectionOpenRequest request,UUID operation,boolean allowExperimental){return session.openConnectionAsync(request,operation,allowExperimental);}
    public OperationFuture<dev.linkgate.contracts.ConnectionReply.Operation> attachConnectionAsync(UUID connection,UUID operation,boolean allowExperimental){return session.connectionActionAsync(connection,operation,true,allowExperimental);}
    public OperationFuture<dev.linkgate.contracts.ConnectionReply.Operation> closeConnectionAsync(UUID connection,UUID operation,boolean allowExperimental){return session.connectionActionAsync(connection,operation,false,allowExperimental);}
    public CompletableFuture<dev.linkgate.contracts.ConnectionReply.Operation> getConnectionOperationAsync(UUID operation){return session.connectionOperationAsync(operation);}
    public CompletableFuture<dev.linkgate.contracts.ConnectionStatus> getConnectionStatusAsync(UUID connection){return session.connectionStatusAsync(connection);}
    public OperationFuture<android.os.ParcelFileDescriptor> attachConnectionDescriptorAsync(UUID stream,UUID attachOperation,IBinder lifetimeToken,boolean allowExperimental){return attachDescriptorAsync(stream,attachOperation,lifetimeToken,allowExperimental,true);}
    private OperationFuture<android.os.ParcelFileDescriptor> attachDescriptorAsync(UUID reference,UUID operation,IBinder lifetimeToken,boolean allowExperimental,boolean formal) {
        if(lifetimeToken==null)throw new IllegalArgumentException("Client lifetime required.");
        ControlSession.NativeCall<android.os.ParcelFileDescriptor> call=()->{
            ILinkGateControl target;synchronized(lock){if(closed||!verified||!bound)throw new IOException("Broker unavailable.");target=remote;}
            dev.linkgate.broker.StreamAttachment attachment=formal?target.attachConnection(reference.toString(),lifetimeToken):target.attachStream(reference.toString(),lifetimeToken);
            if(attachment==null)throw new IOException("Descriptor result missing.");
            if(attachment.status!=0){checkPeer();throw new NativeRefusalException(attachment.status);}
            android.os.ParcelFileDescriptor descriptor=attachment.descriptor;
            if(descriptor==null)throw new IOException("Data attachment unavailable.");return descriptor;
        };ControlSession.Discard<android.os.ParcelFileDescriptor> discard=value->{try{value.close();}catch(IOException ignored){}};
        return formal?session.nativeConnectionAsync(operation,allowExperimental,call,discard):session.nativeAsync(operation,allowExperimental,call,discard);
    }

    private void checkPeer() throws Exception {
        PeerPin.Snapshot current = snapshot(); pin.verify(current);
        synchronized (lock) { if (closed || baseline == null || !PeerPin.same(baseline, current)) throw new SecurityException("Broker identity mismatch."); }
    }

    @SuppressWarnings("deprecation")
    private PeerPin.Snapshot snapshot() throws Exception {
        PackageManager manager = context.getPackageManager();
        ServiceInfo service = manager.getServiceInfo(component, 0);
        int flags = Build.VERSION.SDK_INT >= 28 ? PackageManager.GET_SIGNING_CERTIFICATES : PackageManager.GET_SIGNATURES;
        PackageInfo info = manager.getPackageInfo(pin.packageName, flags);
        if (service.applicationInfo == null || info.applicationInfo == null || service.applicationInfo.uid != info.applicationInfo.uid) throw new SecurityException();
        Signature[] signatures;
        if (Build.VERSION.SDK_INT >= 28) {
            if (info.signingInfo == null) throw new SecurityException();
            signatures = info.signingInfo.getApkContentsSigners();
        } else signatures = info.signatures;
        if (signatures == null || signatures.length == 0 || signatures.length > 32) throw new SecurityException();
        List<String> hashes = new ArrayList<>();
        for (Signature signature : signatures) {
            byte[] bytes = MessageDigest.getInstance("SHA-256").digest(signature.toByteArray()); StringBuilder hash = new StringBuilder(64);
            for (byte value : bytes) { hash.append(Character.forDigit((value >>> 4) & 15, 16)); hash.append(Character.forDigit(value & 15, 16)); }
            hashes.add(hash.toString());
        }
        return new PeerPin.Snapshot(service.packageName, service.name, info.applicationInfo.uid, info.firstInstallTime, info.lastUpdateTime,
            hashes, enabled(manager.getComponentEnabledSetting(component), service.enabled) &&
                enabled(manager.getApplicationEnabledSetting(pin.packageName), info.applicationInfo.enabled), service.exported, info.sharedUserId != null);
    }

    private static boolean enabled(int setting, boolean manifest) {
        return setting == PackageManager.COMPONENT_ENABLED_STATE_ENABLED || (setting == PackageManager.COMPONENT_ENABLED_STATE_DEFAULT && manifest);
    }

    @Override public void close() {
        IBinder old;
        synchronized (lock) {
            if (closed) return; closed = true; old = binder; binder = null; remote = null;
        }
        ready.completeExceptionally(new IOException("Broker connection closed."));
        session.close();
        if (old != null) unlink(old);
        cleanup();
    }

    private void cleanup() {
        boolean unbind;
        synchronized (lock) {
            if (bindInProgress || unbindQueued) return;
            // Even a rejected bind can leave framework tracking associated with the connection.
            unbind = bindingAttempted; unbindQueued = true;
        }
        if (!unbind) { lease.close(); return; }
        Runnable release = () -> { try { context.unbindService(connection); } catch (RuntimeException ignored) { } finally { lease.close(); } };
        if (!MAIN.post(release)) release.run();
    }
    private void unlink(IBinder value) { try { value.unlinkToDeath(death, 0); } catch (RuntimeException ignored) { } }
    private static <T> CompletableFuture<T> failed(Exception error) { CompletableFuture<T> future = new CompletableFuture<>(); future.completeExceptionally(error); return future; }
}
