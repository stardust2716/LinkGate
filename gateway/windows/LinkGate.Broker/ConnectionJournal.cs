using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

namespace LinkGate.Broker;

internal sealed record ConnectionAccepted(LocalAccessScope? Scope,ConnectionStatus Status,bool Created);

public sealed partial class RegistrationStore
{
    private sealed record ConnectionRow(string Caller,Guid Operation,string Request,string Scope,long Sequence,bool Terminal,ConnectionStatus Status);
    private T ConnectionTransaction<T>(Func<SqliteConnection,SqliteTransaction,T> work)=>GrantStorage(()=>
    {lock(gate){using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);var result=work(c,t);t.Commit();return result;}});
    internal ConnectionAccepted BeginConnection(string caller,Guid operation,ConnectionOpenRequest request,Guid boot,ulong generation,DateTimeOffset utc,long admitted,Func<long> clock)=>ConnectionTransaction((c,t)=>
    {
        string json=request.ToJson(),hash=ConnectionDigest("connection.open",json);
        if(RetainedConnectionIntent(c,t,caller,operation,hash) is string retained){var prior=ReadConnectionRow(c,t,caller,null,operation);if(ConnectionResultReference(retained,"connectionId")!=prior.Status.ConnectionId||prior.Request!=json)throw new RegistrationException("STORAGE_UNAVAILABLE");return new ConnectionAccepted(null,prior.Status,false);}
        CheckSecureTime(c,t);var scope=ReadLocalAccess(c,t,caller,request.ApplicationRef,request.ImportAlias,request.DeviceId,request.PublisherAppId,request.RemoteServiceId);
        if(!request.WithinApproved(scope.Import.ConnectionPolicy))throw new RegistrationException("PERMISSION_DENIED");
        if(Convert.ToInt64(Scalar(c,t,"SELECT (SELECT COUNT(*) FROM connections WHERE caller=$caller AND terminal=0)+(SELECT COUNT(*) FROM secure_opens WHERE caller=$caller AND state!='closed')",("$caller",caller)))>=128||Convert.ToInt64(Scalar(c,t,"SELECT (SELECT COUNT(*) FROM connections WHERE terminal=0)+(SELECT COUNT(*) FROM secure_opens WHERE state!='closed')"))>=1024)throw new RegistrationException("RESOURCE_EXHAUSTED");
        AdmitRegistration(c,t,caller);Guid reference=Guid.NewGuid();string owner=(Scalar(c,t,"SELECT root FROM secure_identity WHERE singleton=1") as string??throw new RegistrationException("AUTH_REQUIRED")).ToLowerInvariant();
        var status=new ConnectionMeter(reference,owner,request.DeviceId,request.PublisherAppId,request.RemoteServiceId,boot,generation,utc,admitted,clock).Read().Status;
        Execute(c,t,"INSERT INTO connections VALUES($id,$caller,$operation,$boot,$request,$scope,1,0,$snapshot)",("$id",reference.ToString()),("$caller",caller),("$operation",operation.ToString()),("$boot",boot.ToString()),("$request",json),("$scope",AccessStamp(scope)),("$snapshot",status.ToJson()));
        RecordConnectionIntent(c,t,caller,operation,hash,JsonSerializer.Serialize(new{connectionId=reference}));return new ConnectionAccepted(scope,status,true);
    });
    internal bool RetainedConnectionOpen(string caller,Guid operation,ConnectionOpenRequest request)=>ConnectionTransaction((c,t)=>
    {if(RetainedConnectionIntent(c,t,caller,operation,ConnectionDigest("connection.open",request.ToJson())) is not string retained)return false;var row=ReadConnectionRow(c,t,caller,null,operation);if(ConnectionResultReference(retained,"connectionId")!=row.Status.ConnectionId)throw new RegistrationException("STORAGE_UNAVAILABLE");return true;});
    internal ConnectionStatus ConnectionStatus(string caller,Guid reference)=>ConnectionTransaction((c,t)=>ReadConnectionRow(c,t,caller,reference,null).Status);
    internal ConnectionStatus ConnectionAtOperation(string caller,Guid operation)=>ConnectionTransaction((c,t)=>ReadConnectionRow(c,t,caller,null,operation).Status);
    private static JsonElement? ReadConnectionOperation(SqliteConnection c,SqliteTransaction? t,string caller,Guid operation)
    {
        if(Convert.ToInt64(Scalar(c,t,"SELECT COUNT(*) FROM connections WHERE caller=$caller AND operation=$operation",("$caller",caller),("$operation",operation.ToString())))==0)return null;
        var row=ReadConnectionRow(c,t,caller,null,operation);var status=row.Status;var retained=RetainedConnectionIntent(c,t,caller,operation,ConnectionDigest("connection.open",row.Request));if(retained is null||ConnectionResultReference(retained,"connectionId")!=status.ConnectionId)throw new RegistrationException("STORAGE_UNAVAILABLE");
        if(status.ReadyUtc is not null)return ConnectionSuccess(operation,new{connectionId=status.ConnectionId});
        if(status.ClosedUtc is not null)return JsonSerializer.SerializeToElement(new{operationId=operation,status="failed",error=status.Wire().GetProperty("error")});
        return JsonSerializer.SerializeToElement(new{operationId=operation,status="pending"});
    }
    internal bool PublishConnection(string caller,Guid boot,ConnectionMeter.Snapshot update)=>ConnectionTransaction((c,t)=>PublishConnection(c,t,ReadConnectionRow(c,t,caller,update.Status.ConnectionId,null),boot,update.Sequence,update.Status));
    private static bool PublishConnection(SqliteConnection c,SqliteTransaction t,ConnectionRow prior,Guid boot,long sequence,ConnectionStatus next)
    {
        var old=prior.Status;if(old.BootId!=boot||prior.Terminal||sequence<=prior.Sequence)return false;
        if(old.ConnectionId!=next.ConnectionId||old.BootId!=next.BootId||old.NetworkGeneration!=next.NetworkGeneration||old.OwnerId!=next.OwnerId||old.DeviceId!=next.DeviceId||old.PublisherAppId!=next.PublisherAppId||old.ServiceId!=next.ServiceId||old.AcceptedUtc!=next.AcceptedUtc||old.SelectedPath!="none"&&old.SelectedPath!=next.SelectedPath||old.ReadyUtc is not null&&old.ReadyUtc!=next.ReadyUtc||old.SetupMs is not null&&old.SetupMs!=next.SetupMs||next.DirectTx<old.DirectTx||next.DirectRx<old.DirectRx||next.RelayTx<old.RelayTx||next.RelayRx<old.RelayRx)throw new RegistrationException("STORAGE_UNAVAILABLE");
        if(next.ClosedUtc is null&&Array.IndexOf(new[]{"pending","attaching","open","draining"},next.FlowState)<Array.IndexOf(new[]{"pending","attaching","open","draining"},old.FlowState))throw new RegistrationException("STORAGE_UNAVAILABLE");
        Execute(c,t,"UPDATE connections SET sequence=$sequence,terminal=$terminal,snapshot=$snapshot WHERE reference=$id AND caller=$caller AND boot=$boot AND terminal=0 AND sequence=$prior",("$sequence",sequence),("$terminal",next.ClosedUtc is null?0:1),("$snapshot",next.ToJson()),("$id",old.ConnectionId.ToString()),("$caller",prior.Caller),("$boot",boot.ToString()),("$prior",prior.Sequence));return true;
    }
    internal JsonElement AttachConnection(string caller,Guid operation,Guid reference,Guid boot)=>ConnectionTransaction((c,t)=>
    {
        string hash=ConnectionDigest("connection.attach",reference.ToString());var retained=RetainedConnectionIntent(c,t,caller,operation,hash);if(retained is not null){Guid streamRef=ConnectionResultReference(retained,"streamRef");if(Convert.ToInt64(Scalar(c,t,"SELECT COUNT(*) FROM connection_attachments WHERE reference=$id AND caller=$caller AND operation=$operation AND stream=$stream",("$id",reference.ToString()),("$caller",caller),("$operation",operation.ToString()),("$stream",streamRef.ToString())))!=1)throw new RegistrationException("STORAGE_UNAVAILABLE");return ConnectionSuccess(operation,new{streamRef});}
        var row=ReadConnectionRow(c,t,caller,reference,null);if(row.Terminal||row.Status.BootId!=boot)throw new RegistrationException("CONNECTION_CLOSED");if(row.Status.FlowState!="attaching")throw new RegistrationException("CONNECTION_NOT_READY");CheckConnectionAccess(c,t,row);
        if(Convert.ToInt64(Scalar(c,t,"SELECT COUNT(*) FROM connection_attachments WHERE reference=$id",("$id",reference.ToString())))!=0)throw new RegistrationException("STREAM_ALREADY_ATTACHED");AdmitRegistration(c,t,caller);Guid stream=Guid.NewGuid();
        Execute(c,t,"INSERT INTO connection_attachments VALUES($id,$caller,$operation,$stream,$boot,0)",("$id",reference.ToString()),("$caller",caller),("$operation",operation.ToString()),("$stream",stream.ToString()),("$boot",boot.ToString()));
        string result=JsonSerializer.Serialize(new{streamRef=stream});RecordConnectionIntent(c,t,caller,operation,hash,result);return ConnectionSuccess(operation,Parse(result));
    });
    internal Guid ConsumeConnectionAttachment(string caller,Guid stream,Guid boot)=>ConnectionTransaction((c,t)=>
    {
        object? raw=Scalar(c,t,"SELECT reference FROM connection_attachments WHERE caller=$caller AND stream=$stream AND boot=$boot AND consumed=0",("$caller",caller),("$stream",stream.ToString()),("$boot",boot.ToString()));
        if(raw is not string value||!Guid.TryParseExact(value,"D",out var reference)||reference.ToString()!=value)throw new RegistrationException("AUTH_REQUIRED");
        var row=ReadConnectionRow(c,t,caller,reference,null);if(row.Terminal||row.Status.BootId!=boot||row.Status.FlowState!="attaching")throw new RegistrationException("AUTH_REQUIRED");CheckConnectionAccess(c,t,row);
        Execute(c,t,"UPDATE connection_attachments SET consumed=1 WHERE reference=$id AND caller=$caller AND consumed=0",("$id",value),("$caller",caller));return reference;
    });
    internal JsonElement PrepareConnectionClose(string caller,Guid operation,Guid reference)=>ConnectionTransaction((c,t)=>
    {
        string hash=ConnectionDigest("connection.close",reference.ToString());var retained=RetainedConnectionIntent(c,t,caller,operation,hash);if(retained is not null&&ConnectionResultReference(retained,"connectionId")!=reference)throw new RegistrationException("STORAGE_UNAVAILABLE");if(retained is null){ReadConnectionRow(c,t,caller,reference,null);AdmitRegistration(c,t,caller);RecordConnectionIntent(c,t,caller,operation,hash,JsonSerializer.Serialize(new{connectionId=reference}));}return ConnectionSuccess(operation,new{connectionId=reference});
    });
    internal void TerminateConnection(string caller,Guid reference,string code,DateTimeOffset utc)=>ConnectionTransaction((c,t)=>
    {var row=ReadConnectionRow(c,t,caller,reference,null);if(!row.Terminal)PublishConnection(c,t,row,row.Status.BootId,checked(row.Sequence+1),ConnectionMeter.Recovered(row.Status,code,utc));return true;});
    internal void RecoverConnections(Guid boot,DateTimeOffset utc)
    {
        while(ConnectionTransaction((c,t)=>
        {
            var rows=new List<(string Caller,Guid Reference)>();using(var query=Command(c,t,"SELECT caller,reference FROM connections WHERE terminal=0 AND boot<>$boot LIMIT 32",("$boot",boot.ToString())))using(var reader=query.ExecuteReader())while(reader.Read())rows.Add((reader.GetString(0),Guid.ParseExact(reader.GetString(1),"D")));
            foreach(var value in rows){var row=ReadConnectionRow(c,t,value.Caller,value.Reference,null);PublishConnection(c,t,row,row.Status.BootId,checked(row.Sequence+1),ConnectionMeter.Recovered(row.Status,"GATEWAY_RESTARTED",utc));}return rows.Count;
        })!=0){}
    }
    private static ConnectionRow ReadConnectionRow(SqliteConnection c,SqliteTransaction? t,string caller,Guid? reference,Guid? operation)
    {
        using var query=Command(c,t,"""
            SELECT caller,operation,CASE WHEN length(CAST(request AS BLOB))<=16384 THEN request END,
                CASE WHEN length(CAST(scope AS BLOB))<=32768 THEN scope END,sequence,terminal,
                CASE WHEN length(CAST(snapshot AS BLOB))<=16384 THEN snapshot END,reference,boot
            FROM connections WHERE caller=$caller AND ($reference IS NULL OR reference=$reference) AND ($operation IS NULL OR operation=$operation)
            """,("$caller",caller),("$reference",(object?)reference?.ToString()??DBNull.Value),("$operation",(object?)operation?.ToString()??DBNull.Value));
        using var reader=query.ExecuteReader();if(!reader.Read())throw new RegistrationException("OPERATION_UNKNOWN");
        if(reader.GetValue(4) is not long sequence||sequence<1||reader.GetValue(5) is not long terminal||terminal is <0 or >1)throw new RegistrationException("STORAGE_UNAVAILABLE");
        var status=LinkGate.Contracts.ConnectionStatus.Parse(Encoding.UTF8.GetBytes(reader.GetString(6)));Guid id=Guid.ParseExact(reader.GetString(7),"D"),boot=Guid.ParseExact(reader.GetString(8),"D"),op=Guid.ParseExact(reader.GetString(1),"D");
        if(status.ConnectionId!=id||status.BootId!=boot||(terminal==1)!=(status.ClosedUtc is not null))throw new RegistrationException("STORAGE_UNAVAILABLE");
        var row=new ConnectionRow(reader.GetString(0),op,reader.GetString(2),reader.GetString(3),sequence,terminal==1,status);if(reader.Read())throw new RegistrationException("STORAGE_UNAVAILABLE");return row;
    }
    private void CheckConnectionAccess(SqliteConnection c,SqliteTransaction t,ConnectionRow row)
    {
        CheckSecureTime(c,t);var request=ConnectionOpenRequest.Parse(Encoding.UTF8.GetBytes(row.Request));var scope=ReadLocalAccess(c,t,row.Caller,request.ApplicationRef,request.ImportAlias,request.DeviceId,request.PublisherAppId,request.RemoteServiceId);
        if(AccessStamp(scope)!=row.Scope||!request.WithinApproved(scope.Import.ConnectionPolicy))throw new RegistrationException("AUTH_REQUIRED");
    }
    private static Guid ConnectionResultReference(string value,string kind)
    {
        if(Encoding.UTF8.GetByteCount(value)>256)throw new RegistrationException("STORAGE_UNAVAILABLE");using var document=ControlProtocol.ReadDocument(Encoding.UTF8.GetBytes(value));SecureWire.Fields(document.RootElement,kind);string id=document.RootElement.GetProperty(kind).GetString()??"";
        if(!Guid.TryParseExact(id,"D",out var reference)||reference.ToString()!=id)throw new RegistrationException("STORAGE_UNAVAILABLE");return reference;
    }
    private static string ConnectionDigest(string method,string intent)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method+"\n"+intent)));
    private static JsonElement ConnectionSuccess(Guid operation,object result)=>JsonSerializer.SerializeToElement(new{operationId=operation,status="succeeded",result});
    private static string? RetainedConnectionIntent(SqliteConnection c,SqliteTransaction? t,string caller,Guid operation,string digest)
    {
        using var command=Command(c,t,"SELECT digest,result FROM operations WHERE caller=$caller AND operation=$operation",("$caller",caller),("$operation",operation.ToString()));using var reader=command.ExecuteReader();if(!reader.Read())return null;
        if(reader.GetString(0)!=digest)throw new RegistrationException("OPERATION_CONFLICT");string result=reader.GetString(1);if(reader.Read())throw new RegistrationException("STORAGE_UNAVAILABLE");return result;
    }
    private static void RecordConnectionIntent(SqliteConnection c,SqliteTransaction t,string caller,Guid operation,string hash,string result)=>Execute(c,t,"INSERT INTO operations VALUES($caller,$operation,$hash,$result)",("$caller",caller),("$operation",operation.ToString()),("$hash",hash),("$result",result));
}
