using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

namespace LinkGate.Broker;

public sealed record PendingGrantSnapshot(Guid GrantRequestId,PendingGrantRequest Request,long ApprovedRevision,
    long DecisionVersion,string BoundImportJson,string State)
{
    public bool PendingUserApproval=>true;
}

public sealed partial class RegistrationStore
{
    internal JsonElement RequestPendingGrant(string caller,Guid operation,PendingGrantRequest request) => GrantStorage(() =>
    {
        string normalized=request.ToJson();string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("grant.request\n"+normalized)));
        lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction(deferred:false);VerifyReviewSchema(connection,transaction);
            using(var command=Command(connection,transaction,"SELECT digest,result FROM operations WHERE caller=$caller AND operation=$operation",("$caller",caller),("$operation",operation.ToString())))
            using(var reader=command.ExecuteReader())
            {
                if(reader.Read())
                {
                    if(reader.GetString(0)!=digest)throw new RegistrationException("OPERATION_CONFLICT");
                    return StoredGrantResult(reader.GetString(1));
                }
            }
            var review=OwnedReview(connection,transaction,caller,request.ApplicationRef);
            string? import=review.ApprovedManifest is null?null:ImportJson(review.ApprovedManifest,request.ImportAlias);
            if(import is null || review.ApprovedRevision is null)throw new RegistrationException("AUTH_REQUIRED");
            AdmitRegistration(connection,transaction,caller);
            if(Convert.ToInt64(Scalar(connection,transaction,"SELECT COUNT(*) FROM grant_requests WHERE caller=$caller",("$caller",caller)))>=1024 ||
                Convert.ToInt64(Scalar(connection,transaction,"SELECT COUNT(*) FROM grant_requests"))>=16384)throw new RegistrationException("RESOURCE_EXHAUSTED");
            Guid reference=Guid.NewGuid();string result=JsonSerializer.Serialize(new{grantRequestId=reference.ToString(),pendingUserApproval=true});
            Execute(connection,transaction,"""
                INSERT INTO grant_requests(reference,caller,application,request,approved_revision,decision_version,import_json)
                VALUES($reference,$caller,$application,$request,$revision,$version,$import)
                """,("$reference",reference.ToString()),("$caller",caller),("$application",request.ApplicationRef.ToString()),("$request",normalized),
                ("$revision",review.ApprovedRevision.Value),("$version",review.DecisionVersion),("$import",import));
            Execute(connection,transaction,"INSERT INTO operations(caller,operation,digest,result) VALUES($caller,$operation,$digest,$result)",
                ("$caller",caller),("$operation",operation.ToString()),("$digest",digest),("$result",result));
            transaction.Commit();return StoredGrantResult(result);
        }
    });

    internal JsonElement PendingGrantStatus(string caller,Guid reference)=>GrantStorage(() =>
    {
        lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction(deferred:false);VerifyReviewSchema(connection,transaction);
            var value=ReadGrant(connection,transaction,reference,caller);
            return Parse(JsonSerializer.Serialize(new{grantRequestId=reference.ToString(),state=value.State}));
        }
    });

    /// <summary>Host-only inspection of an unapproved request; remote identity is not authenticated.</summary>
    public PendingGrantSnapshot GetPendingGrantRequest(Guid reference)=>ReviewBoundary(()=>GrantStorage(() =>
    {
        lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction(deferred:false);VerifyReviewSchema(connection,transaction);
            return ReadGrant(connection,transaction,reference,null);
        }
    }));

    public ReviewReferencePage ListPendingGrantReferences(Guid? after=null,int limit=20)
    {
        if(limit is <1 or >200)throw new ArgumentOutOfRangeException(nameof(limit));
        return ReviewBoundary(()=>GrantStorage(() =>
        {
            lock(gate)
            {
                using var connection=Open();using var transaction=connection.BeginTransaction(deferred:false);VerifyReviewSchema(connection,transaction);
                using var command=Command(connection,transaction,"SELECT reference FROM grant_requests WHERE reference>$after ORDER BY reference COLLATE BINARY LIMIT $limit",("$after",after?.ToString()??""),("$limit",limit+1));
                using var reader=command.ExecuteReader();var references=new List<Guid>();var seen=new HashSet<Guid>();
                while(reader.Read())
                {
                    string raw=reader.GetString(0);if(!Guid.TryParseExact(raw,"D",out Guid reference) || reference.ToString()!=raw || !seen.Add(reference))throw new RegistrationException("STORAGE_UNAVAILABLE");
                    references.Add(reference);
                }
                bool more=references.Count>limit;if(more)references.RemoveAt(references.Count-1);
                return new ReviewReferencePage(references.AsReadOnly(),more?references[^1]:null);
            }
        }));
    }

    private ApplicationReviewSnapshot OwnedReview(SqliteConnection connection,SqliteTransaction transaction,string caller,Guid application)
    {
        if(Convert.ToInt64(Scalar(connection,transaction,"SELECT COUNT(*) FROM applications WHERE caller=$caller AND reference=$application",("$caller",caller),("$application",application.ToString())))!=1)
            throw new RegistrationException("AUTH_REQUIRED");
        return ReadReview(connection,transaction,application);
    }
    private PendingGrantSnapshot ReadGrant(SqliteConnection connection,SqliteTransaction transaction,Guid reference,string? caller)
    {
        using var command=Command(connection,transaction,"""
            SELECT CASE WHEN length(CAST(caller AS BLOB))<=256 THEN caller END,
                CASE WHEN length(CAST(application AS BLOB))<=36 THEN application END,
                CASE WHEN length(CAST(request AS BLOB))<=8192 THEN request END,
                approved_revision,decision_version,
                CASE WHEN length(CAST(import_json AS BLOB))<=16384 THEN import_json END FROM grant_requests
            WHERE reference=$reference AND ($caller IS NULL OR caller=$caller) LIMIT 2
            """,("$reference",reference.ToString()),("$caller",(object?)caller??DBNull.Value));
        string scope,raw,application,import;long revision,version;
        using(var reader=command.ExecuteReader())
        {
            if(!reader.Read())throw new RegistrationException("AUTH_REQUIRED");
            scope=reader.GetString(0);application=reader.GetString(1);raw=reader.GetString(2);
            if(reader.GetValue(3) is not long || reader.GetValue(4) is not long)throw new RegistrationException("STORAGE_UNAVAILABLE");
            revision=reader.GetInt64(3);version=reader.GetInt64(4);import=reader.GetString(5);
            if(reader.Read() || revision<1 || version<1 || Encoding.UTF8.GetByteCount(import)>16384)throw new RegistrationException("STORAGE_UNAVAILABLE");
        }
        using var document=ControlProtocol.ReadDocument(Encoding.UTF8.GetBytes(raw));var request=PendingGrantRequest.Parse(document.RootElement);
        if(request.ToJson()!=raw || application!=request.ApplicationRef.ToString())throw new RegistrationException("STORAGE_UNAVAILABLE");
        var wrapper=ManifestContract.Parse(Encoding.UTF8.GetBytes("{\"manifestVersion\":1,\"applicationId\":\"internal.grant-snapshot\",\"displayName\":\"Grant snapshot\",\"exports\":[],\"imports\":["+import+"]}"));
        if(ImportJson(wrapper,request.ImportAlias)!=import)throw new RegistrationException("STORAGE_UNAVAILABLE");
        var current=OwnedReview(connection,transaction,scope,request.ApplicationRef);
        bool matches=current.DecisionVersion==version && current.ApprovedRevision==revision && current.ApprovedManifest is not null && ImportJson(current.ApprovedManifest,request.ImportAlias)==import;
        return new PendingGrantSnapshot(reference,request,revision,version,import,matches?"pending":"superseded");
    }
    private static string? ImportJson(AppManifest manifest,string alias)
    {
        using var document=JsonDocument.Parse(manifest.ToJson());
        foreach(var item in document.RootElement.GetProperty("imports").EnumerateArray())if(item.GetProperty("alias").GetString()==alias)return item.GetRawText();
        return null;
    }
    private static JsonElement StoredGrantResult(string text)
    {using var document=ControlProtocol.ReadDocument(Encoding.UTF8.GetBytes(text));PendingGrantRequest.ValidatePendingResult(document.RootElement);return document.RootElement.Clone();}
    private static T GrantStorage<T>(Func<T> action)
    {
        try{return action();}catch(RegistrationException){throw;}
        catch(Exception e) when(e is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException or ContractException or InvalidOperationException or InvalidCastException or JsonException or FormatException or KeyNotFoundException)
        {throw new RegistrationException("STORAGE_UNAVAILABLE");}
    }
}
