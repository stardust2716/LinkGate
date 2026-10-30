package dev.linkgate.broker;

import android.app.Service;
import android.content.Intent;
import android.os.Binder;
import android.os.IBinder;
import android.os.Process;
import android.os.UserHandle;
import dev.linkgate.contracts.ContractException;
import dev.linkgate.contracts.ControlHello;
import dev.linkgate.contracts.ControlRegistration;
import dev.linkgate.contracts.PendingRegistry;
import com.nebula.linkgate.brokerdev.BuildConfig;

public final class ControlService extends Service {
    public static final String ACTION = "dev.linkgate.BIND_CONTROL_V1";
    private final CallGate calls = new CallGate();
    private final ControlHello hello = new ControlHello();
    private final ControlRegistration registration = new ControlRegistration(hello,this::registry);
    private PendingRegistry registry;
    private synchronized PendingRegistry registry() {
        if(registry==null)registry=new PendingRegistry(new RegistrationDatabase(this));
        return registry;
    }
    private final ILinkGateControl.Stub binder = new ILinkGateControl.Stub() {
        @Override public StreamAttachment attachConnection(String streamRef,IBinder lifetimeToken) {return new StreamAttachment(5,null);}
        @Override public StreamAttachment attachStream(String operationId,IBinder lifetimeToken) {
            return new StreamAttachment(5,null);
        }
        @Override public byte[] request(byte[] message) {
            final int uid = Binder.getCallingUid();
            final UserHandle user = Binder.getCallingUserHandle();
            if (!Process.myUserHandle().equals(user)) throw new SecurityException("Caller profile unavailable.");
            if (message == null || message.length == 0 || message.length > ControlHello.MAX_BYTES)
                throw new IllegalArgumentException("INVALID_REQUEST");
            CallGate.Lease lease = calls.acquire(uid);
            if (lease == null) throw new IllegalStateException("RESOURCE_EXHAUSTED");
            try (CallGate.Lease active = lease) {
                final String caller;
                try { caller = CallerIdentity.capture(getPackageManager(), uid); }
                catch (RuntimeException e) { throw new SecurityException("AUTH_REQUIRED"); }
                try { return BuildConfig.ENABLE_PENDING_REGISTRATION?registration.respond(message,caller):hello.respond(message); }
                catch (ContractException e) { throw new IllegalArgumentException("INVALID_REQUEST"); }
            }
        }
    };
    @Override public IBinder onBind(Intent intent) {
        return intent != null && ACTION.equals(intent.getAction()) ? binder : null;
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        stopSelf(startId); return START_NOT_STICKY;
    }
}
