using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using LinkGate.Broker;
using LinkGate.Control;

namespace LinkGate.Review;

public sealed record ReviewIntent(Guid OperationId,Guid ApplicationRef,long PendingRevision,long DecisionVersion,
    ApplicationReviewDecision Decision,bool Acknowledged=false);

public interface IReviewIntentJournal
{
    ReviewIntent? Read();
    void Prepare(ReviewIntent intent);
    void Acknowledge(Guid operationId);
}

public sealed class ReviewJournalException(string code) : Exception("Review journal unavailable.")
{ public string Code { get; }=code; }

public sealed class ReviewIntentFile : IReviewIntentJournal,IDisposable
{
    private const int MaxBytes=2048;
    private readonly string directory,path;
    private readonly FileStream lease;
    private bool disposed;
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    public ReviewIntentFile(string directory)
    {
        this.directory=Path.GetFullPath(directory);path=Path.Combine(this.directory,".last-review-intent.json");
        try
        {
            Guard(path);string lockPath=Path.Combine(this.directory,".review-console.lock");Guard(lockPath);
            lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        }
        catch(IOException){throw new ReviewJournalException("REVIEW_BUSY_OR_UNAVAILABLE");}
        catch(UnauthorizedAccessException){throw new ReviewJournalException("REVIEW_JOURNAL_UNAVAILABLE");}
    }
    public ReviewIntent? Read()=>Protected(() =>
    {
        Guard(path);if(!File.Exists(path))return null;
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length is <1 or >MaxBytes)throw new InvalidDataException();
        byte[] bytes=new byte[(int)file.Length];file.ReadExactly(bytes);if(file.ReadByte()!=-1)throw new InvalidDataException();
        using var document=ControlJson.ReadDocument(bytes);var value=document.RootElement;
        string[] names=["operationId","applicationRef","pendingRevision","decisionVersion","decision","acknowledged"];
        if(value.ValueKind!=JsonValueKind.Object || !value.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(names.Order()))throw new InvalidDataException();
        bool Integer(string name,out long n){var raw=value.GetProperty(name);n=0;return raw.ValueKind==JsonValueKind.Number && raw.GetRawText().All(c=>c is >= '0' and <= '9') && raw.TryGetInt64(out n);}
        if(!Guid.TryParseExact(value.GetProperty("operationId").GetString(),"D",out Guid operation) ||
            !Guid.TryParseExact(value.GetProperty("applicationRef").GetString(),"D",out Guid reference) ||
            !Integer("pendingRevision",out long revision) || revision<1 || !Integer("decisionVersion",out long version) ||
            !Integer("decision",out long decision) || decision is <1 or >3 ||
            value.GetProperty("acknowledged").ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException();
        return new ReviewIntent(operation,reference,revision,version,(ApplicationReviewDecision)decision,value.GetProperty("acknowledged").GetBoolean());
    });
    public void Prepare(ReviewIntent intent)=>Protected(() =>
    {
        if(intent.Acknowledged || intent.PendingRevision<1 || intent.DecisionVersion<0 ||
            intent.Decision is not (ApplicationReviewDecision.Approved or ApplicationReviewDecision.Denied or ApplicationReviewDecision.Revoked))throw new InvalidDataException();
        if(Read() is {Acknowledged:false})throw new ReviewJournalException("REVIEW_RECOVERY_REQUIRED");
        Write(intent);return true;
    });
    public void Acknowledge(Guid operationId)=>Protected(() =>
    {
        ReviewIntent current=Read()??throw new InvalidDataException();
        if(current.OperationId!=operationId)throw new InvalidDataException();
        if(!current.Acknowledged)Write(current with{Acknowledged=true});return true;
    });
    private void Write(ReviewIntent intent)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(intent,JsonOptions);if(bytes.Length>MaxBytes)throw new InvalidDataException();
        string temporary=Path.Combine(directory,".review-intent-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            Guard(path);Guard(temporary);
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {file.Write(bytes);file.Flush(true);}
            Guard(path);File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private void Guard(string file)
    {
        for(var item=new DirectoryInfo(directory);item is not null;item=item.Parent)
            if(Attributes(item.FullName) is FileAttributes attributes && (attributes&FileAttributes.ReparsePoint)!=0)throw new IOException();
        PrivateAcl(new DirectoryInfo(directory));
        // GetAttributes also sees directory links in a position reserved for a file.
        if(Attributes(file) is FileAttributes value)
        {
            if((value&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new IOException();
            PrivateAcl(new FileInfo(file));
        }
    }
    private static void PrivateAcl(FileSystemInfo item)
    {
        using var identity=WindowsIdentity.GetCurrent();var user=identity.User??throw new UnauthorizedAccessException();
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        var administrators=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        FileSystemSecurity acl=item is DirectoryInfo folder?folder.GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner):
            ((FileInfo)item).GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
        var owner=acl.GetOwner(typeof(SecurityIdentifier));
        if(owner is null || (!owner.Equals(user)&&!owner.Equals(system)&&!owner.Equals(administrators)) ||
            (item is DirectoryInfo && !acl.AreAccessRulesProtected))throw new UnauthorizedAccessException();
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow && !rule.IdentityReference.Equals(user)&&!rule.IdentityReference.Equals(system))
                throw new UnauthorizedAccessException();
    }
    private static FileAttributes? Attributes(string path)
    {try{return File.GetAttributes(path);}catch(FileNotFoundException){return null;}catch(DirectoryNotFoundException){return null;}}
    private T Protected<T>(Func<T> action)
    {
        if(disposed)throw new ReviewJournalException("REVIEW_JOURNAL_UNAVAILABLE");
        try{return action();}
        catch(ReviewJournalException){throw;}
        catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {throw new ReviewJournalException("REVIEW_JOURNAL_UNAVAILABLE");}
    }
    public void Dispose(){if(disposed)return;disposed=true;lease.Dispose();}
}
