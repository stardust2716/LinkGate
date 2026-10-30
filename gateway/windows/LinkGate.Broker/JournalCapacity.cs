using Microsoft.Data.Sqlite;

namespace LinkGate.Broker;

/// <summary>Host-only logical journal occupancy; this does not reserve disk space.</summary>
public sealed record JournalCapacity(long RetainedOperations, long RetainedHostOperations, long ReservedRevocations,
    int GlobalLimit, int HostLimit)
{
    public long AvailableGlobalOperations => Math.Max(0L, (long)GlobalLimit-RetainedOperations-ReservedRevocations);
    public long AvailableHostOperations => Math.Max(0L, (long)HostLimit-RetainedHostOperations-ReservedRevocations);
    public bool WithinLimits => RetainedOperations <= (long)GlobalLimit-ReservedRevocations &&
        RetainedHostOperations <= (long)HostLimit-ReservedRevocations;
}

public sealed partial class RegistrationStore
{
    /// <summary>Trusted host aggregate diagnostic. Never dispatch this through application RPC.</summary>
    public JournalCapacity GetJournalCapacity() => ReviewBoundary(() =>
    {
        lock (gate)
        {
            using var connection=Open(); using var transaction=connection.BeginTransaction(deferred:false);
            VerifyReviewSchema(connection,transaction);
            return ReadCapacity(connection,transaction);
        }
    });

    private JournalCapacity ReadCapacity(SqliteConnection connection,SqliteTransaction transaction) => new(
        Convert.ToInt64(Scalar(connection,transaction,"SELECT COUNT(*) FROM operations")),
        Convert.ToInt64(Scalar(connection,transaction,"SELECT COUNT(*) FROM operations WHERE caller=$caller OR caller=$secure",("$caller",ReviewAdministrator),("$secure",SecureAdministrator))),
        // Count either half conservatively if a damaged row has lost one approved field.
        Convert.ToInt64(Scalar(connection,transaction,"SELECT (SELECT COUNT(*) FROM application_reviews WHERE approved_manifest IS NOT NULL OR approved_revision IS NOT NULL)+(SELECT COUNT(*) FROM secure_peers WHERE active=1)+(SELECT COUNT(*) FROM secure_grants WHERE active=1)+(SELECT COUNT(*) FROM local_access WHERE active=1)")),
        totalLimit,callerLimit);

    private void CheckExistingCapacity(SqliteConnection connection,SqliteTransaction transaction)
    {
        if(!ReadCapacity(connection,transaction).WithinLimits) throw new RegistrationException("RESOURCE_EXHAUSTED");
    }

    private void AdmitRegistration(SqliteConnection connection,SqliteTransaction transaction,string caller)
    {
        var capacity=ReadCapacity(connection,transaction);
        if(!capacity.WithinLimits || capacity.AvailableGlobalOperations<1 ||
            Convert.ToInt64(Scalar(connection,transaction,"SELECT COUNT(*) FROM operations WHERE caller=$caller",("$caller",caller)))>=callerLimit)
            throw new RegistrationException("RESOURCE_EXHAUSTED");
    }

    private void AdmitReview(SqliteConnection connection,SqliteTransaction transaction,bool wasApproved,bool willBeApproved)
    {
        // Approval creates a reservation; revocation converts that same slot into a retained result.
        int cost=1+(willBeApproved?1:0)-(wasApproved?1:0);
        var capacity=ReadCapacity(connection,transaction);
        if(!capacity.WithinLimits || capacity.AvailableGlobalOperations<cost || capacity.AvailableHostOperations<cost)
            throw new RegistrationException("RESOURCE_EXHAUSTED");
    }
}
