using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

namespace LinkGate.Broker;

public enum ApplicationReviewDecision { None, Approved, Denied, Revoked }

/// <summary>A host-only immutable review. Local approval is not a device or service grant.</summary>
public sealed class ApplicationReviewSnapshot
{
    internal string Origin { get; }
    internal string Caller { get; }
    /// <summary>Host-visible scope provenance; not proof of a signed binary identity.</summary>
    public string CallerScope => Caller;
    public Guid ApplicationRef { get; }
    public AppManifest PendingManifest { get; }
    public long PendingRevision { get; }
    public long DecisionVersion { get; }
    public long AuthorityEpoch { get; }
    public long ReviewedRevision { get; }
    public ApplicationReviewDecision LastDecision { get; }
    public AppManifest? ApprovedManifest { get; }
    public long? ApprovedRevision { get; }
    internal ApplicationReviewSnapshot(string origin, string caller, Guid reference, AppManifest pending, long revision,
        long version, long reviewed, ApplicationReviewDecision decision, AppManifest? approved, long? approvedRevision,long authorityEpoch)
    {
        Origin=origin; Caller=caller; ApplicationRef=reference; PendingManifest=pending; PendingRevision=revision;
        DecisionVersion=version;AuthorityEpoch=authorityEpoch; ReviewedRevision=reviewed; LastDecision=decision; ApprovedManifest=approved; ApprovedRevision=approvedRevision;
    }
}

public sealed record ApplicationReviewResult(Guid ApplicationRef, long PendingRevision, long DecisionVersion,
    ApplicationReviewDecision Decision, long? ApprovedRevision);

public sealed record ReviewReferencePage(IReadOnlyList<Guid> Items, Guid? NextCursor);

public sealed class ApplicationReviewException : Exception
{
    public string Code { get; }
    internal ApplicationReviewException(string code) : base("Application review rejected.") { Code=code; }
}

public sealed partial class RegistrationStore
{
    // Actual pipe caller scopes are OS-derived hexadecimal hashes, never this reserved host scope.
    private const string ReviewAdministrator = "@linkgate-host-review-v1";

    /// <summary>Host-only bounded keyset enumeration. Concurrent insertions may require a refresh.</summary>
    public ReviewReferencePage ListReviewReferences(Guid? after=null,int limit=20)
    {
        if(limit is <1 or >200) throw new ArgumentOutOfRangeException(nameof(limit));
        return ReviewBoundary(() =>
        {
            lock(gate)
            {
                using var connection=Open();using var transaction=connection.BeginTransaction(deferred:false);
                VerifyReviewSchema(connection,transaction);
                using var command=Command(connection,transaction,"SELECT reference FROM applications WHERE reference>$after ORDER BY reference COLLATE BINARY LIMIT $limit",
                    ("$after",after?.ToString()??""),("$limit",limit+1));
                using var reader=command.ExecuteReader();var references=new List<Guid>();var seen=new HashSet<Guid>();
                while(reader.Read())
                {
                    string text=reader.GetString(0);
                    if(!Guid.TryParseExact(text,"D",out var reference) || reference.ToString()!=text || !seen.Add(reference))
                        throw new RegistrationException("STORAGE_UNAVAILABLE");
                    references.Add(reference);
                }
                bool more=references.Count>limit;if(more)references.RemoveAt(references.Count-1);
                return new ReviewReferencePage(references.AsReadOnly(),more?references[^1]:null);
            }
        });
    }

    /// <summary>Trusted in-process host only. Never dispatch this through application RPC.</summary>
    public ApplicationReviewSnapshot GetReview(Guid applicationRef) => ReviewBoundary(() =>
    {
        lock (gate)
        {
            using var connection=Open(); using var transaction=connection.BeginTransaction(deferred:false);
            VerifyReviewSchema(connection,transaction);
            return ReadReview(connection,transaction,applicationRef);
        }
    });

    /// <summary>Call only after the host presents the snapshot and receives an explicit user decision.</summary>
    public ApplicationReviewResult DecideReview(ApplicationReviewSnapshot snapshot, ApplicationReviewDecision decision, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (decision is not (ApplicationReviewDecision.Approved or ApplicationReviewDecision.Denied or ApplicationReviewDecision.Revoked))
            throw new ArgumentOutOfRangeException(nameof(decision));
        if (!StringComparer.OrdinalIgnoreCase.Equals(snapshot.Origin,directory)) throw new ApplicationReviewException("REVIEW_STALE");
        string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            "host.app.review",snapshot.Caller,snapshot.ApplicationRef.ToString(),
            snapshot.PendingRevision.ToString(CultureInfo.InvariantCulture),snapshot.DecisionVersion.ToString(CultureInfo.InvariantCulture),
            snapshot.PendingManifest.ToJson(),decision.ToString()))));
        return ReviewBoundary(() =>
        {
            lock (gate)
            {
                using var connection=Open(); using var transaction=connection.BeginTransaction(deferred:false);
                VerifyReviewSchema(connection,transaction);
                using (var command=Command(connection,transaction,"SELECT digest,result FROM operations WHERE caller=$caller AND operation=$op",
                    ("$caller",ReviewAdministrator),("$op",operationId.ToString())))
                using (var reader=command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        if (reader.GetString(0)!=digest) throw new RegistrationException("OPERATION_CONFLICT");
                        var prior=ParseReviewResult(reader.GetString(1));
                        ValidateReviewResult(prior,snapshot,decision);
                        return prior;
                    }
                }
                var current=ReadReview(connection,transaction,snapshot.ApplicationRef);
                if (current.Caller!=snapshot.Caller || current.PendingRevision!=snapshot.PendingRevision ||
                    current.DecisionVersion!=snapshot.DecisionVersion || current.PendingManifest.ToJson()!=snapshot.PendingManifest.ToJson())
                    throw new RegistrationException("REVIEW_STALE");
                if (current.DecisionVersion==long.MaxValue) throw new RegistrationException("RESOURCE_EXHAUSTED");
                long version=current.DecisionVersion+1;
                if(decision!=ApplicationReviewDecision.Denied&&current.AuthorityEpoch==long.MaxValue)throw new RegistrationException("RESOURCE_EXHAUSTED");
                long epoch=current.AuthorityEpoch+(decision==ApplicationReviewDecision.Denied?0:1);
                long? approvedRevision=decision==ApplicationReviewDecision.Approved?current.PendingRevision:
                    decision==ApplicationReviewDecision.Revoked?null:current.ApprovedRevision;
                string? approved=decision==ApplicationReviewDecision.Approved?current.PendingManifest.ToJson():
                    decision==ApplicationReviewDecision.Revoked?null:current.ApprovedManifest?.ToJson();
                AdmitReview(connection,transaction,current.ApprovedManifest is not null,approved is not null);
                Execute(connection,transaction,"""
                    UPDATE application_reviews SET decision_version=$version,authority_epoch=$epoch,reviewed_revision=$reviewed,decision=$decision,
                        approved_revision=$revision,approved_manifest=$manifest WHERE caller=$caller AND app=$app
                    """,("$version",version),("$epoch",epoch),("$reviewed",current.PendingRevision),("$decision",decision.ToString()),
                    ("$revision",(object?)approvedRevision??DBNull.Value),("$manifest",(object?)approved??DBNull.Value),
                    ("$caller",current.Caller),("$app",current.PendingManifest.ApplicationId));
                var result=new ApplicationReviewResult(current.ApplicationRef,current.PendingRevision,version,decision,approvedRevision);
                Execute(connection,transaction,"INSERT INTO operations(caller,operation,digest,result) VALUES($caller,$op,$digest,$result)",
                    ("$caller",ReviewAdministrator),("$op",operationId.ToString()),("$digest",digest),("$result",JsonSerializer.Serialize(result)));
                transaction.Commit(); return result;
            }
        });
    }

    /// <summary>Host-only recovery of an original operation result; never re-applies the decision.</summary>
    public ApplicationReviewResult GetReviewOperation(Guid operationId) => ReviewBoundary(() =>
    {
        lock (gate)
        {
            using var connection=Open(); using var transaction=connection.BeginTransaction(deferred:false);
            VerifyReviewSchema(connection,transaction);
            string result=Scalar(connection,transaction,"SELECT result FROM operations WHERE caller=$caller AND operation=$op",
                ("$caller",ReviewAdministrator),("$op",operationId.ToString())) as string??throw new RegistrationException("OPERATION_UNKNOWN");
            return ParseReviewResult(result);
        }
    });

    private ApplicationReviewSnapshot ReadReview(SqliteConnection connection,SqliteTransaction transaction,Guid reference)
    {
        try{return ReadReviewCore(connection,transaction,reference);}
        catch(RegistrationException){throw;}
        catch(Exception e) when(e is SqliteException or InvalidOperationException or FormatException or InvalidCastException or OverflowException)
        {throw new RegistrationException("STORAGE_UNAVAILABLE");}
    }

    private ApplicationReviewSnapshot ReadReviewCore(SqliteConnection connection,SqliteTransaction transaction,Guid reference)
    {
        using var command=Command(connection,transaction,"""
            SELECT a.caller,a.app,a.manifest,r.pending_revision,r.decision_version,r.reviewed_revision,r.decision,
                r.approved_revision,r.approved_manifest,r.authority_epoch FROM applications a LEFT JOIN application_reviews r
                ON a.caller=r.caller AND a.app=r.app WHERE a.reference=$reference LIMIT 2
            """,("$reference",reference.ToString()));
        using var reader=command.ExecuteReader();
        if (!reader.Read()) throw new RegistrationException("APPLICATION_UNKNOWN");
        string caller=reader.GetString(0),app=reader.GetString(1);
        AppManifest pending=StoredManifest(reader.GetString(2),app);
        for(int i=3;i<=5;i++) if(reader.GetValue(i) is not long) throw new RegistrationException("STORAGE_UNAVAILABLE");
        long revision=reader.GetInt64(3),version=reader.GetInt64(4),reviewed=reader.GetInt64(5);
        string rawDecision=reader.GetString(6);
        if (!Enum.TryParse<ApplicationReviewDecision>(rawDecision,out var decision) || decision.ToString()!=rawDecision || !Enum.IsDefined(decision))
            throw new RegistrationException("STORAGE_UNAVAILABLE");
        if (!reader.IsDBNull(7) && reader.GetValue(7) is not long) throw new RegistrationException("STORAGE_UNAVAILABLE");
        long? approvedRevision=reader.IsDBNull(7)?null:reader.GetInt64(7);
        AppManifest? approved=reader.IsDBNull(8)?null:StoredManifest(reader.GetString(8),app);
        if(reader.GetValue(9) is not long epoch||epoch<0||epoch>version||(approved is not null&&epoch<1))throw new RegistrationException("STORAGE_UNAVAILABLE");
        if (reader.Read() || revision<1 || version<0 || reviewed<0 || reviewed>revision ||
            ((approvedRevision is null)!=(approved is null)) || (approvedRevision is not null && (approvedRevision<1 || approvedRevision>revision)) ||
            (approvedRevision==revision && approved?.ToJson()!=pending.ToJson()) ||
            (version==0 && (decision!=ApplicationReviewDecision.None || reviewed!=0 || approved is not null)) ||
            (version>0 && (decision==ApplicationReviewDecision.None || reviewed<1)) ||
            (decision==ApplicationReviewDecision.Approved && (approvedRevision!=reviewed || approved is null)) ||
            (decision==ApplicationReviewDecision.Revoked && approved is not null) || approvedRevision>reviewed)
            throw new RegistrationException("STORAGE_UNAVAILABLE");
        return new ApplicationReviewSnapshot(directory,caller,reference,pending,revision,version,reviewed,decision,approved,approvedRevision,epoch);
    }

    private static AppManifest StoredManifest(string raw,string app)
    {
        try
        {
            var manifest=ManifestContract.Parse(Encoding.UTF8.GetBytes(raw));
            if(manifest.ApplicationId!=app || manifest.ToJson()!=raw) throw new ContractException();
            return manifest;
        }
        catch(ContractException){throw new RegistrationException("STORAGE_UNAVAILABLE");}
    }
    private static void VerifyReviewSchema(SqliteConnection connection,SqliteTransaction transaction)
    {
        if (Convert.ToInt64(Scalar(connection,transaction,"PRAGMA user_version"))!=8) throw new RegistrationException("STORAGE_UNAVAILABLE");
        foreach (var table in new[] {
            ("applications",new[]{"caller","app","reference","manifest"}),
            ("operations",new[]{"caller","operation","digest","result"}),
            ("application_reviews",new[]{"caller","app","pending_revision","decision_version","reviewed_revision","decision","approved_revision","approved_manifest","authority_epoch"}),
            ("grant_requests",new[]{"reference","caller","application","request","approved_revision","decision_version","import_json"}),
            ("secure_identity",new[]{"singleton","device","root","max_utc"}),
            ("secure_peers",new[]{"peer","certificate","revision","active","host","port"}),
            ("secure_grants",new[]{"reference","peer","client","publisher","application","service","peer_revision","publisher_revision","decision_version","revision","active"}),
            ("secure_opens",new[]{"caller","operation","connection","boot","state","error"}),
            ("local_access",new[]{"reference","caller","application","alias","peer","publisher","service","approved_revision","authority_epoch","peer_revision","import_json","revision","active"}),
            ("connections",new[]{"reference","caller","operation","boot","request","scope","sequence","terminal","snapshot"}),
            ("connection_attachments",new[]{"reference","caller","operation","stream","boot","consumed"}) })
        {
            using var command=Command(connection,transaction,"PRAGMA table_info("+table.Item1+")");
            using var reader=command.ExecuteReader();var fields=new List<string>();while(reader.Read())fields.Add(reader.GetString(1));
            if (!fields.SequenceEqual(table.Item2)) throw new RegistrationException("STORAGE_UNAVAILABLE");
        }
    }
    private static ApplicationReviewResult ParseReviewResult(string json)
    {
        try
        {
            using var document=ControlProtocol.ReadDocument(Encoding.UTF8.GetBytes(json));var value=document.RootElement;
            string[] fields=["ApplicationRef","PendingRevision","DecisionVersion","Decision","ApprovedRevision"];
            if(value.ValueKind!=JsonValueKind.Object || !value.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(fields.Order())) throw new JsonException();
            bool Number(string name,out long n){var v=value.GetProperty(name);n=0;return v.ValueKind==JsonValueKind.Number && v.GetRawText().All(c=>c is >= '0' and <= '9') && v.TryGetInt64(out n);}
            if (!Guid.TryParseExact(value.GetProperty("ApplicationRef").GetString(),"D",out var reference) ||
                !Number("PendingRevision",out long revision) || revision<1 || !Number("DecisionVersion",out long version) || version<1 ||
                !Number("Decision",out long code) || code<1 || code>3) throw new JsonException();
            long? approved=null;if(value.GetProperty("ApprovedRevision").ValueKind!=JsonValueKind.Null){if(!Number("ApprovedRevision",out long a) || a<1 || a>revision)throw new JsonException();approved=a;}
            var decision=(ApplicationReviewDecision)code;
            if ((decision==ApplicationReviewDecision.Approved && approved!=revision) || (decision==ApplicationReviewDecision.Revoked && approved is not null)) throw new JsonException();
            return new ApplicationReviewResult(reference,revision,version,decision,approved);
        }
        catch(Exception e) when(e is JsonException or InvalidDataException or InvalidOperationException or FormatException or KeyNotFoundException)
        {throw new RegistrationException("STORAGE_UNAVAILABLE");}
    }
    private static void ValidateReviewResult(ApplicationReviewResult result,ApplicationReviewSnapshot snapshot,ApplicationReviewDecision decision)
    {
        long? approved=decision==ApplicationReviewDecision.Approved?snapshot.PendingRevision:decision==ApplicationReviewDecision.Revoked?null:snapshot.ApprovedRevision;
        if (snapshot.DecisionVersion==long.MaxValue || result.ApplicationRef!=snapshot.ApplicationRef || result.PendingRevision!=snapshot.PendingRevision ||
            result.DecisionVersion!=snapshot.DecisionVersion+1 || result.Decision!=decision || result.ApprovedRevision!=approved)
            throw new RegistrationException("STORAGE_UNAVAILABLE");
    }
    private static T ReviewBoundary<T>(Func<T> action)
    {
        try{return action();}
        catch(RegistrationException e){throw new ApplicationReviewException(e.Code);}
        catch(Exception e) when(e is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or OverflowException or InvalidCastException)
        {throw new ApplicationReviewException("STORAGE_UNAVAILABLE");}
    }
}
