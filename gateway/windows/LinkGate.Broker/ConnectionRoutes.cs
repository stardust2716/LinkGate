using System.Security.Cryptography;
using System.Text;
namespace LinkGate.Broker;

// Trusted Agent composition only. Applications never construct or receive this value.
public sealed class ConnectionRouteScope
{
    internal LocalAccessScope Authority {get;}
    public string Key {get;}
    public Guid ApplicationRef=>Authority.Application;
    public string ClientApplication=>Authority.Client;
    public string ImportAlias=>Authority.Alias;
    public string PublisherDevice=>Authority.Peer.DeviceId;
    public string PublisherApplication=>Authority.Publisher;
    public string Service=>Authority.Service;
    public bool AllowRelay=>Authority.Import.ConnectionPolicy.AllowRelay;
    public int CreationTimeoutMs=>Authority.Import.ConnectionPolicy.ConnectTimeoutMs;
    internal ConnectionRouteScope(LocalAccessScope authority)
    {Authority=authority;Key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RegistrationStore.AccessStamp(authority)))).ToLowerInvariant();}
}
public sealed class ConnectionRouteException : IOException
{
    public string Code {get;}
    public ConnectionRouteException(string code):base("Private route unavailable."){if(code is not ("RESOURCE_EXHAUSTED" or "CLOCK_UNTRUSTED" or "CAPABILITY_UNSUPPORTED"))throw new ArgumentException();Code=code;}
}
public interface IConnectionRoute : IDisposable
{
    int LoopbackPort {get;}
    CancellationToken Cancellation {get;}
}
public interface IConnectionRoutes : IAsyncDisposable
{
    string DeviceId {get;}
    bool SupportsDirect {get;}
    bool SupportsRelay {get;}
    ValueTask<IConnectionRoute> OpenAsync(ConnectionRouteScope scope,bool direct,long deadlineMilliseconds,CancellationToken cancellation);
    void Invalidate();
}
public sealed partial class RegistrationStore
{
    public bool IsConnectionRouteScopeCurrent(ConnectionRouteScope scope)=>scope is not null&&LocalAccessCurrent([scope.Authority])[0];
}
