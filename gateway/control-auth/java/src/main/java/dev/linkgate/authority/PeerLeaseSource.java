package dev.linkgate.authority;

import dev.linkgate.transport.PeerClient;
import java.io.IOException;
import java.util.concurrent.atomic.AtomicBoolean;

/** One source belongs to one peer opening/lifetime; never share it across peers. */
public final class PeerLeaseSource implements PeerClient.LeaseSource {
    private final ControlClient client;
    private final AuthoritySnapshot initial;
    private final ServerLease.Window window = new ServerLease.Window();
    private final AtomicBoolean closed = new AtomicBoolean();
    private ControlClient.Authorized current;
    private final Object gate = new Object();
    private PeerLeaseSource(ControlClient client, ControlClient.Authorized lease) throws LeaseRejectedException {
        this.client = client; current = lease; initial = lease.receipt.authority; window.accept(lease.receipt);
    }
    public static PeerLeaseSource acquire(ControlClient client, String application, AuthoritySnapshot.Target target, String protocol, String authority) throws IOException, LeaseRejectedException {
        ControlClient.Authorized lease = client.acquire(application, target);
        if (!lease.receipt.authority.protocol.equals(protocol) || !lease.receipt.authority.authority.equals(authority == null ? "" : authority)) throw new LeaseRejectedException();
        return new PeerLeaseSource(client, lease);
    }
    @Override public String token() throws IOException {
        try {
            ControlClient.Authorized observed;
            synchronized (gate) { if (closed.get()) throw new LeaseRejectedException(); window.current(); observed = current; }
            if (observed.receipt.remainingMillis() <= 60000) {
                // No lock is held during network I/O: peer cancellation can close
                // this generation immediately while a bounded refresh is pending.
                ControlClient.Authorized next = client.acquire(initial.scope.visitorApp, initial.scope.target);
                synchronized (gate) {
                    if (closed.get() || !same(initial, next.receipt.authority)) throw new LeaseRejectedException();
                    ServerLease.Receipt accepted = window.accept(next.receipt);
                    if (accepted == next.receipt) current = next;
                }
            }
            synchronized (gate) { if (closed.get()) throw new LeaseRejectedException(); window.current(); return current.token; }
        } catch (LeaseRejectedException | IOException failure) { close(); throw new IOException("Server authority unavailable."); }
    }
    private static boolean same(AuthoritySnapshot a, AuthoritySnapshot b) {
        return a.scope.equals(b.scope) && a.visitorRevision.equals(b.visitorRevision) && a.publisherRevision.equals(b.publisherRevision) && a.serviceRevision.equals(b.serviceRevision) && a.grantRevision.equals(b.grantRevision) && a.protocol.equals(b.protocol) && a.authority.equals(b.authority);
    }
    @Override public void close() { closed.set(true); window.close(); }
}
