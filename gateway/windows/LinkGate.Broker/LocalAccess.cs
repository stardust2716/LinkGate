using System.Text.Json;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

namespace LinkGate.Broker;

/// <summary>Trusted-host snapshot of one pending request; not an application credential.</summary>
public sealed record LocalAccessProposal(Guid RequestId,string CallerScope,Guid ApplicationRef,string ImportAlias,
    string DeviceId,string PublisherAppId,string ServiceId,long ApprovedRevision,long DecisionVersion,long AuthorityEpoch,
    long PeerRevision,string BoundImportJson,Guid? GrantId,long GrantRevision);

public sealed record LocalAccessSnapshot(Guid Reference,string CallerScope,Guid ApplicationRef,string ImportAlias,string DeviceId,string PublisherAppId,string ServiceId,long Revision,bool Active,bool Current,string BoundImportJson);

internal sealed record LocalAccessScope(string Caller,Guid Application,long Revision,long AuthorityEpoch,string Client,
    string Alias,string ImportJson,ImportService Import,SecurePeer Peer,string Publisher,string Service,Guid AccessReference,long AccessRevision);

public sealed partial class RegistrationStore
{
    internal static string AccessStamp(LocalAccessScope scope)=>JsonSerializer.Serialize(new{scope.Caller,scope.Application,scope.Revision,scope.AuthorityEpoch,scope.Client,scope.Alias,scope.ImportJson,device=scope.Peer.DeviceId,peerRevision=scope.Peer.Revision,scope.Publisher,scope.Service,scope.AccessReference,scope.AccessRevision});
    public ReviewReferencePage ListLocalAccessReferences(Guid? after=null,int limit=20)
    {
        if(limit is <1 or >200)throw new ArgumentOutOfRangeException(nameof(limit));return ReviewBoundary(()=>GrantStorage(()=>
        {
            lock(gate){using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);using var query=Command(c,t,"SELECT reference FROM local_access WHERE reference>$after ORDER BY reference COLLATE BINARY LIMIT $limit",("$after",after?.ToString()??""),("$limit",limit+1));using var reader=query.ExecuteReader();var references=new List<Guid>();
                while(reader.Read()){string raw=reader.GetString(0);if(!Guid.TryParseExact(raw,"D",out var value)||value.ToString()!=raw)throw new RegistrationException("STORAGE_UNAVAILABLE");references.Add(value);}
                bool more=references.Count>limit;if(more)references.RemoveAt(references.Count-1);return new ReviewReferencePage(references.AsReadOnly(),more?references[^1]:null);}
        }));
    }
    public LocalAccessSnapshot GetLocalAccess(Guid reference)=>ReviewBoundary(()=>GrantStorage(()=>
    {
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);var row=AccessByReference(c,t,reference);
            string caller,alias,device,publisher,service;Guid application;
            using(var query=Command(c,t,"SELECT CASE WHEN length(CAST(caller AS BLOB))<=256 THEN caller END,application,alias,peer,publisher,service FROM local_access WHERE reference=$id",("$id",reference.ToString())))using(var reader=query.ExecuteReader())
            {if(!reader.Read())throw new RegistrationException("AUTH_REQUIRED");caller=reader.GetString(0);string raw=reader.GetString(1);if(!Guid.TryParseExact(raw,"D",out application)||application.ToString()!=raw)throw new RegistrationException("STORAGE_UNAVAILABLE");alias=reader.GetString(2);device=reader.GetString(3);publisher=reader.GetString(4);service=reader.GetString(5);}
            var target=new PendingGrantRequest(application,alias,device,publisher,service);if(target.DeviceId.Length!=64||target.DeviceId.Any(x=>!(x is >= '0' and <= '9' or >= 'a' and <= 'f')))throw new RegistrationException("STORAGE_UNAVAILABLE");
            bool current=false;try{CheckSecureTime(c,t);current=ReadLocalAccess(c,t,caller,application,alias,device,publisher,service).AccessRevision==row.Revision;}catch(RegistrationException error)when(error.Code=="AUTH_REQUIRED"){}
            t.Commit();return new LocalAccessSnapshot(reference,caller,application,alias,device,publisher,service,row.Revision,row.Active,current,row.ImportJson);
        }
    }));
    public LocalAccessProposal ProposeLocalAccess(Guid request)=>ReviewBoundary(()=>GrantStorage(()=>
    {
        lock(gate){using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);CheckSecureTime(c,t);
            var result=ReadLocalAccessProposal(c,t,request);t.Commit();return result;}
    }));
    private LocalAccessProposal ReadLocalAccessProposal(SqliteConnection c,SqliteTransaction t,Guid request)
    {
        string caller=Scalar(c,t,"SELECT CASE WHEN length(CAST(caller AS BLOB))<=256 THEN caller END FROM grant_requests WHERE reference=$request",("$request",request.ToString())) as string??throw new RegistrationException("AUTH_REQUIRED");
        var pending=ReadGrant(c,t,request,caller);var scope=pending.Request;
        if(pending.State!="pending")throw new RegistrationException("REVIEW_STALE");
        if(scope.DeviceId.Length!=64||scope.DeviceId.Any(ch=>!((ch>='0'&&ch<='9')||(ch>='a'&&ch<='f'))))throw new RegistrationException("AUTH_REQUIRED");
        var review=OwnedReview(c,t,caller,scope.ApplicationRef);var peer=ReadSecurePeer(c,t,scope.DeviceId);
        var import=review.ApprovedManifest?.Imports.SingleOrDefault(x=>x.Alias==scope.ImportAlias);
        if(peer?.Active!=true||review.ApprovedRevision is null||import?.EndpointMode!="brokered")throw new RegistrationException("AUTH_REQUIRED");
        var prior=ReadAccess(c,t,caller,scope.ApplicationRef,scope.ImportAlias,scope.DeviceId,scope.PublisherAppId,scope.RemoteServiceId);
        return new LocalAccessProposal(request,caller,scope.ApplicationRef,scope.ImportAlias,scope.DeviceId,scope.PublisherAppId,scope.RemoteServiceId,
            review.ApprovedRevision.Value,review.DecisionVersion,review.AuthorityEpoch,peer.Revision,pending.BoundImportJson,prior?.Reference,prior?.Revision??0);
    }
    public SecureAuthorityResult ApproveLocalAccess(LocalAccessProposal proposal,Guid operation)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return SecureMutation("access.approve",JsonSerializer.Serialize(proposal),operation,(c,t)=>
        {
            if(ReadLocalAccessProposal(c,t,proposal.RequestId)!=proposal)throw new RegistrationException("REVIEW_STALE");
            var prior=ReadAccess(c,t,proposal.CallerScope,proposal.ApplicationRef,proposal.ImportAlias,proposal.DeviceId,proposal.PublisherAppId,proposal.ServiceId);
            if(proposal.GrantRevision>=long.MaxValue-1)throw new RegistrationException("RESOURCE_EXHAUSTED");
            if(prior is null&&Convert.ToInt64(Scalar(c,t,"SELECT (SELECT COUNT(*) FROM secure_grants)+(SELECT COUNT(*) FROM local_access)"))>=4096)throw new RegistrationException("RESOURCE_EXHAUSTED");
            AdmitReview(c,t,prior?.Active==true,true);Guid reference=prior?.Reference??Guid.NewGuid();long revision=proposal.GrantRevision+1;
            Execute(c,t,"""
                INSERT INTO local_access VALUES($id,$caller,$application,$alias,$peer,$publisher,$service,$approved,$epoch,$peerVersion,$import,$revision,1)
                ON CONFLICT(reference) DO UPDATE SET approved_revision=excluded.approved_revision,authority_epoch=excluded.authority_epoch,
                    peer_revision=excluded.peer_revision,import_json=excluded.import_json,revision=excluded.revision,active=1
                """,("$id",reference.ToString()),("$caller",proposal.CallerScope),("$application",proposal.ApplicationRef.ToString()),("$alias",proposal.ImportAlias),
                ("$peer",proposal.DeviceId),("$publisher",proposal.PublisherAppId),("$service",proposal.ServiceId),("$approved",proposal.ApprovedRevision),
                ("$epoch",proposal.AuthorityEpoch),("$peerVersion",proposal.PeerRevision),("$import",proposal.BoundImportJson),("$revision",revision));
            return new SecureAuthorityResult(reference.ToString(),revision,true,0);
        });
    }
    public SecureAuthorityResult RevokeLocalAccess(Guid reference,long expectedRevision,Guid operation)=>SecureMutation("access.revoke",JsonSerializer.Serialize(new{reference,expectedRevision}),operation,(c,t)=>
    {
        var prior=AccessByReference(c,t,reference);if(prior.Revision!=expectedRevision)throw new RegistrationException("REVIEW_STALE");
        if(prior.Revision==long.MaxValue)throw new RegistrationException("RESOURCE_EXHAUSTED");AdmitReview(c,t,prior.Active,false);
        Execute(c,t,"UPDATE local_access SET active=0,revision=revision+1 WHERE reference=$id",("$id",reference.ToString()));
        return new SecureAuthorityResult(reference.ToString(),prior.Revision+1,false,prior.Active?1:0);
    });
    internal LocalAccessScope ResolveLocalAccess(string caller,ConnectionOpenRequest request)=>GrantStorage(()=>
    {
        lock(gate){using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);CheckSecureTime(c,t);
            var scope=ReadLocalAccess(c,t,caller,request.ApplicationRef,request.ImportAlias,request.DeviceId,request.PublisherAppId,request.RemoteServiceId);
            if(!request.WithinApproved(scope.Import.ConnectionPolicy))throw new RegistrationException("PERMISSION_DENIED");t.Commit();return scope;}
    });
    private LocalAccessScope ReadLocalAccess(SqliteConnection c,SqliteTransaction t,string caller,Guid application,string alias,string peer,string publisher,string service)
    {
        var review=OwnedReview(c,t,caller,application);var import=review.ApprovedManifest?.Imports.SingleOrDefault(x=>x.Alias==alias);var device=ReadSecurePeer(c,t,peer);
        if(review.ApprovedRevision is null||import?.EndpointMode!="brokered"||device?.Active!=true)throw new RegistrationException("AUTH_REQUIRED");
        var access=ReadAccess(c,t,caller,application,alias,peer,publisher,service);string currentImport=ImportJson(review.ApprovedManifest!,alias)!;
        if(access?.Active!=true||access.ApprovedRevision!=review.ApprovedRevision||access.Epoch!=review.AuthorityEpoch||access.PeerRevision!=device.Revision||access.ImportJson!=currentImport)throw new RegistrationException("AUTH_REQUIRED");
        return new LocalAccessScope(caller,application,review.ApprovedRevision.Value,review.AuthorityEpoch,review.ApprovedManifest!.ApplicationId,alias,currentImport,import,device,publisher,service,access.Reference,access.Revision);
    }
    internal bool[] LocalAccessCurrent(LocalAccessScope[] scopes)
    {
        var result=new bool[scopes.Length];if(scopes.Length==0)return result;
        try
        {
            lock(gate){using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);CheckSecureTime(c,t);
                for(int i=0;i<scopes.Length;i++)try{var expected=scopes[i];var current=ReadLocalAccess(c,t,expected.Caller,expected.Application,expected.Alias,expected.Peer.DeviceId,expected.Publisher,expected.Service);
                    result[i]=current.AccessReference==expected.AccessReference&&current.AccessRevision==expected.AccessRevision&&current.Revision==expected.Revision&&current.AuthorityEpoch==expected.AuthorityEpoch&&current.ImportJson==expected.ImportJson&&current.Peer==expected.Peer;
                }catch(RegistrationException){}t.Commit();return result;}
        }catch(Exception){Array.Clear(result);return result;}
    }
    private sealed record AccessRow(Guid Reference,long ApprovedRevision,long Epoch,long PeerRevision,string ImportJson,long Revision,bool Active);
    private static AccessRow? ReadAccess(SqliteConnection c,SqliteTransaction t,string caller,Guid app,string alias,string peer,string publisher,string service)
    {
        using var command=Command(c,t,"""
            SELECT reference,approved_revision,authority_epoch,peer_revision,CASE WHEN length(CAST(import_json AS BLOB))<=16384 THEN import_json END,revision,active
            FROM local_access WHERE caller=$caller AND application=$app AND alias=$alias AND peer=$peer AND publisher=$publisher AND service=$service
            """,("$caller",caller),("$app",app.ToString()),("$alias",alias),("$peer",peer),("$publisher",publisher),("$service",service));
        using var reader=command.ExecuteReader();if(!reader.Read())return null;return AccessRecord(reader);
    }
    private static AccessRow AccessByReference(SqliteConnection c,SqliteTransaction t,Guid reference)
    {
        using var command=Command(c,t,"SELECT reference,approved_revision,authority_epoch,peer_revision,CASE WHEN length(CAST(import_json AS BLOB))<=16384 THEN import_json END,revision,active FROM local_access WHERE reference=$id",("$id",reference.ToString()));
        using var reader=command.ExecuteReader();if(!reader.Read())throw new RegistrationException("AUTH_REQUIRED");return AccessRecord(reader);
    }
    private static AccessRow AccessRecord(SqliteDataReader reader)
    {
        string id=reader.GetString(0);if(!Guid.TryParseExact(id,"D",out var reference)||reference.ToString()!=id)throw new RegistrationException("STORAGE_UNAVAILABLE");
        long Positive(int index){if(reader.GetValue(index) is not long value||value<1)throw new RegistrationException("STORAGE_UNAVAILABLE");return value;}
        long approved=Positive(1),epoch=Positive(2),peer=Positive(3),revision=Positive(5);
        if(reader.GetValue(6) is not long active||active is <0 or >1||(active==1&&revision==long.MaxValue)||reader.IsDBNull(4))throw new RegistrationException("STORAGE_UNAVAILABLE");
        string import=reader.GetString(4);using var document=ControlProtocol.ReadDocument(System.Text.Encoding.UTF8.GetBytes(import));if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new RegistrationException("STORAGE_UNAVAILABLE");
        if(reader.Read())throw new RegistrationException("STORAGE_UNAVAILABLE");return new AccessRow(reference,approved,epoch,peer,import,revision,active==1);
    }
}
