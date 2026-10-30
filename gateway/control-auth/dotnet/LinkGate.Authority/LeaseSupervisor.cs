namespace LinkGate.Authority;

public sealed class LeaseGuard : IDisposable
{
    private readonly object gate = new();
    private readonly LeaseWindow window = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Action<LeaseGuard> remove;
    private readonly AuthoritySnapshot initial;
    private AuthorizedLease current;
    private bool stopped;
    public CancellationToken Cancellation => lifetime.Token;
    internal LeaseGuard(AuthorizedLease lease, Action<LeaseGuard> remove) { current = lease; initial = lease.Receipt.Authority; this.remove = remove; window.Accept(lease.Receipt); }
    public AuthorizedLease Current
    {
        get { lock (gate) { if (stopped) throw new LeaseRejectedException(); _ = window.Current; return current; } }
    }
    public bool Active { get { try { _ = Current; return true; } catch (LeaseRejectedException) { Dispose(); return false; } } }
    public void Renew(AuthorizedLease lease)
    {
        lock (gate)
        {
            if (stopped || initial != lease.Receipt.Authority) { Dispose(); throw new LeaseRejectedException(); }
            var accepted = window.Accept(lease.Receipt);
            if (ReferenceEquals(accepted, lease.Receipt)) current = lease;
        }
    }
    public void Dispose()
    {
        lock (gate) { if (stopped) return; stopped = true; window.Close(); }
        remove(this); lifetime.Cancel();
    }
}

public sealed class LeaseSupervisor : IAsyncDisposable
{
    public ControlClient Client { get; }
    private readonly HashSet<LeaseGuard> active = [];
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task monitor;
    private bool closed;
    public LeaseSupervisor(ControlClient client) { Client = client; monitor = MonitorAsync(); }
    public LeaseGuard Register(AuthorizedLease lease)
    {
        Client.CheckReceipt(lease.Receipt);
        lock (gate)
        {
            if (closed) throw new LeaseRejectedException();
            if (active.Count >= 128) throw new ControlRefusedException("RESOURCE_EXHAUSTED");
            var guard = new LeaseGuard(lease, Remove); active.Add(guard); return guard;
        }
    }
    private void Remove(LeaseGuard guard) { lock (gate) active.Remove(guard); }
    private async Task MonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                LeaseGuard[] snapshot; lock (gate) snapshot = active.ToArray();
                var ready = snapshot.Where(value => value.Active).ToArray();
                await Task.WhenAll(ready.Chunk(64).Select(CheckBatch));
            }
        }
        catch (OperationCanceledException) { }
        finally { LeaseGuard[] snapshot; lock (gate) snapshot = active.ToArray(); foreach (var guard in snapshot) guard.Dispose(); }
    }
    private async Task CheckBatch(LeaseGuard[] guards)
    {
        // Snapshot before I/O. A later renewal cannot make a previously revoked
        // authorization current, and a closed guard is never revived by this reply.
        var pending = new List<(LeaseGuard Guard, LeaseReceipt Receipt)>();
        foreach (var guard in guards) { try { pending.Add((guard, guard.Current.Receipt)); } catch (LeaseRejectedException) { guard.Dispose(); } }
        if (pending.Count == 0) return;
        try
        {
            var valid = await Client.CheckAsync(pending.Select(value => value.Receipt).ToArray(), lifetime.Token);
            for (int i = 0; i < pending.Count; i++) if (!valid[i]) pending[i].Guard.Dispose();
        }
        catch (Exception failure) when (failure is ControlRefusedException or LeaseRejectedException or OperationCanceledException)
        { foreach (var value in pending) value.Guard.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (closed) return; closed = true; }
        lifetime.Cancel(); await monitor; lifetime.Dispose();
    }
}
