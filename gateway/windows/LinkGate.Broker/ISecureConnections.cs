using System.Text.Json;
namespace LinkGate.Broker;

public interface ISecureConnections
{
    JsonElement Open(string caller,Guid operation,Guid application,string alias,string peer,string publisher,string service);
    JsonElement Status(string caller,Guid operation);
    JsonElement Close(string caller,Guid operation);
}

public interface IRelaySecureConnections : ISecureConnections
{
    bool SupportsRelay { get; }
    JsonElement OpenRelay(string caller,Guid operation,Guid application,string alias,string peer,string publisher,string service);
}
