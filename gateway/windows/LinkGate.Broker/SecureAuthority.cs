using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

namespace LinkGate.Broker;

public sealed record SecurePeer(string DeviceId,string CertificatePem,long Revision,bool Active,string? Host,int? Port);
public sealed record SecureGrantProposal(string Peer,string ClientApplication,Guid PublisherApplication,string Service,
    long PeerRevision,long PublisherRevision,long DecisionVersion,Guid? GrantId,long GrantRevision);
public sealed record SecureAuthorityResult(string Reference,long Revision,bool Active,int AffectedGrants);
public sealed record SecureAuthorization(Guid GrantId,long GrantRevision,string Peer,long PeerRevision,Guid PublisherApplication,
    long AuthorityEpoch,int Port,string Protocol,string? LogicalAuthority);

public sealed partial class RegistrationStore
{
    private const string SecureAdministrator="@linkgate-secure-authority-v1";
    internal TimeProvider SecureClock{get;set;}=TimeProvider.System;
    public void BindSecureIdentity(SecureIdentity identity)=>ReviewBoundary(()=>
    {
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);
            string root=Convert.ToHexString(SHA256.HashData(identity.Root.RawData));
            using(var command=Command(c,t,"SELECT device,root FROM secure_identity WHERE singleton=1"))
            using(var reader=command.ExecuteReader())
            {
                if(reader.Read())
                {if(reader.GetString(0)!=identity.DeviceId || reader.GetString(1)!=root)throw new RegistrationException("IDENTITY_CHANGED");}
                else
                {
                    if(Convert.ToInt64(Scalar(c,t,"SELECT (SELECT COUNT(*) FROM secure_peers)+(SELECT COUNT(*) FROM secure_grants)"))!=0)throw new RegistrationException("STORAGE_UNAVAILABLE");
                    Execute(c,t,"INSERT INTO secure_identity VALUES(1,$device,$root,$utc)",("$device",identity.DeviceId),("$root",root),("$utc",SecureClock.GetUtcNow().UtcTicks));
                }
            }
            CheckSecureTime(c,t);t.Commit();return true;
        }
    });
    private void CheckSecureTime(SqliteConnection c,SqliteTransaction t)
    {
        var raw=Scalar(c,t,"SELECT max_utc FROM secure_identity WHERE singleton=1");
        if(raw is not long previous)throw new RegistrationException("AUTH_REQUIRED");long now=SecureClock.GetUtcNow().UtcTicks;
        if(now<previous-TimeSpan.FromMinutes(2).Ticks)throw new RegistrationException("CLOCK_UNTRUSTED");
        if(now>previous+TimeSpan.FromSeconds(30).Ticks)Execute(c,t,"UPDATE secure_identity SET max_utc=$utc WHERE singleton=1",("$utc",now));
    }
    public SecurePeer? GetSecurePeer(string peer)=>ReviewBoundary(()=>
    {lock(gate){using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);return ReadSecurePeer(c,t,peer);}});
    private static SecurePeer? ReadSecurePeer(SqliteConnection c,SqliteTransaction t,string peer)
    {
        using var command=Command(c,t,"SELECT certificate,revision,active,host,port FROM secure_peers WHERE peer=$peer",("$peer",peer));using var reader=command.ExecuteReader();
        if(!reader.Read())return null;
        if(reader.GetValue(1) is not long revision || revision<1 || reader.GetValue(2) is not long active || active is <0 or >1 || (active==1&&revision==long.MaxValue))throw new RegistrationException("STORAGE_UNAVAILABLE");
        string? host=reader.IsDBNull(3)?null:reader.GetString(3);int? port=reader.IsDBNull(4)?null:reader.GetInt32(4);
        if((host is null)!=(port is null) || (host is not null && (!IPAddress.TryParse(host,out var ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||port is <1 or >65535)))throw new RegistrationException("STORAGE_UNAVAILABLE");
        return new SecurePeer(peer,reader.GetString(0),revision,active==1,host,port);
    }
    public bool IsSecurePeerActive(string peer)
    {
        try{lock(gate){using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);CheckSecureTime(c,t);bool active=ReadSecurePeer(c,t,peer)?.Active==true;t.Commit();return active;}}
        catch(Exception){return false;}
    }
    public SecureAuthorityResult TrustSecurePeer(SecureIdentity owner,string certificatePem,long expectedRevision,Guid operation,string? host=null,int? port=null)
    {
        using var certificate=X509Certificate2.CreateFromPem(certificatePem);SecureIdentity.ValidateDeviceCertificate(certificate,owner.Root);
        string peer=SecureIdentity.KeyId(certificate);string canonical=certificate.ExportCertificatePem();
        if((host is null)!=(port is null) || (host is not null && (!IPAddress.TryParse(host,out var address)||address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||address.Equals(IPAddress.Any)||port is <1 or >65535)))throw new ArgumentException("Invalid owner peer endpoint.");
        string intent=JsonSerializer.Serialize(new{peer,certificate=canonical,expectedRevision,host,port});
        return SecureMutation("device.trust",intent,operation,(c,t)=>
        {
            string boundRoot=Scalar(c,t,"SELECT root FROM secure_identity WHERE singleton=1") as string??"";
            if(boundRoot!=Convert.ToHexString(SHA256.HashData(owner.Root.RawData)))throw new RegistrationException("IDENTITY_CHANGED");
            var prior=ReadSecurePeer(c,t,peer);if((prior?.Revision??0)!=expectedRevision)throw new RegistrationException("REVIEW_STALE");
            if(expectedRevision>=long.MaxValue-1)throw new RegistrationException("RESOURCE_EXHAUSTED");
            if(prior is null && Convert.ToInt64(Scalar(c,t,"SELECT COUNT(*) FROM secure_peers"))>=64)throw new RegistrationException("RESOURCE_EXHAUSTED");
            AdmitReview(c,t,prior?.Active==true,true);long revision=expectedRevision+1;
            Execute(c,t,"""
                INSERT INTO secure_peers VALUES($peer,$certificate,$revision,1,$host,$port)
                ON CONFLICT(peer) DO UPDATE SET certificate=excluded.certificate,revision=excluded.revision,active=1,host=excluded.host,port=excluded.port
                """,("$peer",peer),("$certificate",canonical),("$revision",revision),("$host",(object?)host??DBNull.Value),("$port",(object?)port??DBNull.Value));
            return new SecureAuthorityResult(peer,revision,true,0);
        });
    }
    public SecureGrantProposal ProposeSecureGrant(string peer,string clientApplication,Guid publisherApplication,string service)=>ReviewBoundary(()=>
    {ValidateSecureScope(peer,clientApplication,service);lock(gate){using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);CheckSecureTime(c,t);var result=ProposeGrant(c,t,peer,clientApplication,publisherApplication,service);t.Commit();return result;}});
    private SecureGrantProposal ProposeGrant(SqliteConnection c,SqliteTransaction t,string peer,string client,Guid app,string service)
    {
        var device=ReadSecurePeer(c,t,peer);var review=ReadReview(c,t,app);
        if(device?.Active!=true || review.ApprovedManifest is null || review.ApprovedRevision is null || !review.ApprovedManifest.Exports.Any(e=>e.ServiceId==service) || review.DecisionVersion>=long.MaxValue)throw new RegistrationException("AUTH_REQUIRED");
        Guid? id=null;long revision=0;
        using(var command=Command(c,t,"SELECT reference,revision FROM secure_grants WHERE peer=$peer AND client=$client AND publisher=$publisher AND service=$service",
            ("$peer",peer),("$client",client),("$publisher",review.ApprovedManifest.ApplicationId),("$service",service)))
        using(var reader=command.ExecuteReader()){if(reader.Read()){id=Guid.ParseExact(reader.GetString(0),"D");if(reader.GetValue(1) is not long n || n<1)throw new RegistrationException("STORAGE_UNAVAILABLE");revision=n;}}
        return new SecureGrantProposal(peer,client,app,service,device.Revision,review.ApprovedRevision.Value,review.DecisionVersion,id,revision);
    }
    public SecureAuthorityResult ApproveSecureGrant(SecureGrantProposal proposal,Guid operation)=>SecureMutation("grant.approve",JsonSerializer.Serialize(proposal),operation,(c,t)=>
    {
        var current=ProposeGrant(c,t,proposal.Peer,proposal.ClientApplication,proposal.PublisherApplication,proposal.Service);
        if(current!=proposal)throw new RegistrationException("REVIEW_STALE");
        if(proposal.GrantRevision>=long.MaxValue-1)throw new RegistrationException("RESOURCE_EXHAUSTED");
        bool active=proposal.GrantId is Guid prior && Convert.ToInt64(Scalar(c,t,"SELECT active FROM secure_grants WHERE reference=$id",("$id",prior.ToString())))==1;
        if(proposal.GrantId is null && Convert.ToInt64(Scalar(c,t,"SELECT (SELECT COUNT(*) FROM secure_grants)+(SELECT COUNT(*) FROM local_access)"))>=4096)throw new RegistrationException("RESOURCE_EXHAUSTED");
        AdmitReview(c,t,active,true);Guid reference=proposal.GrantId??Guid.NewGuid();long revision=proposal.GrantRevision+1;
        var reviewed=ReadReview(c,t,proposal.PublisherApplication);var publisher=reviewed.ApprovedManifest!;
        Execute(c,t,"""
            INSERT INTO secure_grants VALUES($id,$peer,$client,$publisher,$application,$service,$peerRevision,$publisherRevision,$decisionVersion,$revision,1)
            ON CONFLICT(reference) DO UPDATE SET application=excluded.application,peer_revision=excluded.peer_revision,publisher_revision=excluded.publisher_revision,
                decision_version=excluded.decision_version,revision=excluded.revision,active=1
            """,("$id",reference.ToString()),("$peer",proposal.Peer),("$client",proposal.ClientApplication),("$publisher",publisher.ApplicationId),
            ("$application",proposal.PublisherApplication.ToString()),("$service",proposal.Service),("$peerRevision",proposal.PeerRevision),
            ("$publisherRevision",proposal.PublisherRevision),("$decisionVersion",reviewed.AuthorityEpoch),("$revision",revision));
        return new SecureAuthorityResult(reference.ToString(),revision,true,0);
    });
    public SecureAuthorityResult RevokeSecureGrant(Guid reference,long expectedRevision,Guid operation)=>SecureMutation("grant.revoke",JsonSerializer.Serialize(new{reference,expectedRevision}),operation,(c,t)=>
    {
        using var command=Command(c,t,"SELECT revision,active FROM secure_grants WHERE reference=$id",("$id",reference.ToString()));long revision;bool active;
        using(var reader=command.ExecuteReader()){if(!reader.Read())throw new RegistrationException("AUTH_REQUIRED");revision=reader.GetInt64(0);active=reader.GetInt64(1)==1;}
        if(revision!=expectedRevision)throw new RegistrationException("REVIEW_STALE");if(revision==long.MaxValue)throw new RegistrationException("RESOURCE_EXHAUSTED");
        AdmitReview(c,t,active,false);Execute(c,t,"UPDATE secure_grants SET active=0,revision=revision+1 WHERE reference=$id",("$id",reference.ToString()));
        return new SecureAuthorityResult(reference.ToString(),revision+1,false,active?1:0);
    });
    public SecureAuthorityResult RevokeSecurePeer(string peer,long expectedRevision,Guid operation)=>SecureMutation("device.revoke",JsonSerializer.Serialize(new{peer,expectedRevision}),operation,(c,t)=>
    {
        var device=ReadSecurePeer(c,t,peer)??throw new RegistrationException("AUTH_REQUIRED");if(device.Revision!=expectedRevision)throw new RegistrationException("REVIEW_STALE");
        if(device.Revision==long.MaxValue)throw new RegistrationException("RESOURCE_EXHAUSTED");
        long grants=Convert.ToInt64(Scalar(c,t,"SELECT (SELECT COUNT(*) FROM secure_grants WHERE peer=$peer AND active=1)+(SELECT COUNT(*) FROM local_access WHERE peer=$peer AND active=1)",("$peer",peer)));
        var capacity=ReadCapacity(c,t);int released=(device.Active?1:0)+(int)grants;
        if(!capacity.WithinLimits || capacity.AvailableGlobalOperations<1-released || capacity.AvailableHostOperations<1-released)throw new RegistrationException("RESOURCE_EXHAUSTED");
        Execute(c,t,"UPDATE secure_peers SET active=0,revision=revision+1 WHERE peer=$peer; UPDATE secure_grants SET active=0,revision=revision+1 WHERE peer=$peer AND active=1; UPDATE local_access SET active=0,revision=revision+1 WHERE peer=$peer AND active=1",("$peer",peer));
        return new SecureAuthorityResult(peer,device.Revision+1,false,(int)grants);
    });
    internal SecureAuthorization AuthorizeSecureOpen(string peer,string client,string publisher,string service,string protocol,string? authority)=>GrantStorage(()=>
    {
        ValidateSecureScope(peer,client,service);
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);CheckSecureTime(c,t);
            var result=ReadSecureAuthorization(c,t,peer,client,publisher,service,protocol,authority);t.Commit();return result;
        }
    });
    private SecureAuthorization ReadSecureAuthorization(SqliteConnection c,SqliteTransaction t,string peer,string client,string publisher,string service,string protocol,string? authority)
    {
            var device=ReadSecurePeer(c,t,peer);
            if(device?.Active!=true)throw new RegistrationException("AUTH_REQUIRED");
            using var command=Command(c,t,"SELECT reference,revision,application,peer_revision,publisher_revision,decision_version FROM secure_grants WHERE peer=$peer AND client=$client AND publisher=$publisher AND service=$service AND active=1",
                ("$peer",peer),("$client",client),("$publisher",publisher),("$service",service));
            Guid id,app;long version,peerVersion,pubVersion,decision;
            using(var reader=command.ExecuteReader())
            {
                if(!reader.Read())throw new RegistrationException("AUTH_REQUIRED");id=Guid.ParseExact(reader.GetString(0),"D");version=reader.GetInt64(1);app=Guid.ParseExact(reader.GetString(2),"D");
                peerVersion=reader.GetInt64(3);pubVersion=reader.GetInt64(4);decision=reader.GetInt64(5);
            }
            if(version<1 || version==long.MaxValue || peerVersion<1 || pubVersion<1 || decision<1)throw new RegistrationException("AUTH_REQUIRED");
            var review=ReadReview(c,t,app);var export=review.ApprovedManifest?.Exports.SingleOrDefault(e=>e.ServiceId==service);
            if(device.Revision!=peerVersion || review.AuthorityEpoch!=decision || review.ApprovedRevision!=pubVersion || review.ApprovedManifest?.ApplicationId!=publisher ||
                export is null || export.Protocol!=protocol || export.LogicalAuthority!=authority)throw new RegistrationException("AUTH_REQUIRED");
            return new SecureAuthorization(id,version,peer,peerVersion,app,decision,export.LocalTarget.Port,export.Protocol,export.LogicalAuthority);
    }
    internal bool[] SecureBatchCurrent((SecureAuthorization Authorization,SecureOpenScope Scope)[] values)
    {
        var result=new bool[values.Length];if(values.Length==0)return result;
        try
        {
            lock(gate)
            {
                using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);CheckSecureTime(c,t);
                for(int i=0;i<values.Length;i++)
                {
                    var (expected,scope)=values[i];
                    try{result[i]=ReadSecureAuthorization(c,t,expected.Peer,scope.Client,scope.Publisher,scope.Service,expected.Protocol,expected.LogicalAuthority)==expected;}
                    catch(RegistrationException){result[i]=false;}
                }
                t.Commit();return result;
            }
        }
        catch(Exception){Array.Clear(result);return result;}
    }
    internal bool SecureAuthorizationCurrent(SecureAuthorization expected,string client,string publisher,string service)
    {try{return AuthorizeSecureOpen(expected.Peer,client,publisher,service,expected.Protocol,expected.LogicalAuthority)==expected;}catch(Exception){return false;}}
    private static void ValidateSecureScope(string peer,string client,string service)
    {
        if(peer.Length!=64 || peer.Any(c=>!((c>='0'&&c<='9')||(c>='a'&&c<='f'))))throw new RegistrationException("AUTH_REQUIRED");
        _=new PendingGrantRequest(Guid.Empty,service,peer,client,service);
    }
    public SecureAuthorityResult GetSecureAuthorityOperation(Guid operation)=>ReviewBoundary(()=>GrantStorage(()=>
    {
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);
            string text=Scalar(c,t,"SELECT result FROM operations WHERE caller=$caller AND operation=$operation",("$caller",SecureAdministrator),("$operation",operation.ToString())) as string??throw new RegistrationException("OPERATION_UNKNOWN");
            return ParseSecureResult(text,null);
        }
    }));
    private static SecureAuthorityResult ParseSecureResult(string text,string? method)
    {
        using var document=ControlProtocol.ReadDocument(Encoding.UTF8.GetBytes(text));var value=document.RootElement;
        SecureWire.Fields(value,"Reference","Revision","Active","AffectedGrants");var previous=JsonSerializer.Deserialize<SecureAuthorityResult>(value);
        if(previous is null||previous.Reference is null||previous.Revision<1||(previous.Active&&previous.Revision==long.MaxValue)||previous.AffectedGrants is <0 or >4096)throw new RegistrationException("STORAGE_UNAVAILABLE");
        bool peerReference=previous.Reference.Length==64&&previous.Reference.All(ch=>(ch>='0'&&ch<='9')||(ch>='a'&&ch<='f'));
        bool grantReference=Guid.TryParseExact(previous.Reference,"D",out _);
        if(!peerReference&&!grantReference)throw new RegistrationException("STORAGE_UNAVAILABLE");
        if(method is not null)
        {
            bool peerResult=method.StartsWith("device.",StringComparison.Ordinal),revoked=method.EndsWith("revoke",StringComparison.Ordinal);
            if(peerResult!=peerReference||previous.Active==revoked||(!revoked&&previous.AffectedGrants!=0)||(!peerResult&&previous.AffectedGrants>1))throw new RegistrationException("STORAGE_UNAVAILABLE");
        }
        return previous;
    }
    private SecureAuthorityResult SecureMutation(string method,string intent,Guid operation,Func<SqliteConnection,SqliteTransaction,SecureAuthorityResult> action)=>ReviewBoundary(()=>GrantStorage(()=>
    {
        string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method+"\n"+intent)));
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);
            using(var command=Command(c,t,"SELECT digest,result FROM operations WHERE caller=$caller AND operation=$operation",("$caller",SecureAdministrator),("$operation",operation.ToString())))
            using(var reader=command.ExecuteReader())
            {
                if(reader.Read())
                {
                    if(reader.GetString(0)!=digest)throw new RegistrationException("OPERATION_CONFLICT");
                    return ParseSecureResult(reader.GetString(1),method);
                }
            }
            CheckSecureTime(c,t);var result=action(c,t);
            Execute(c,t,"INSERT INTO operations VALUES($caller,$operation,$digest,$result)",("$caller",SecureAdministrator),("$operation",operation.ToString()),("$digest",digest),("$result",JsonSerializer.Serialize(result)));
            t.Commit();return result;
        }
    }));
}
