using System.Text.Json;
using LinkGate.Contracts;
namespace LinkGate.Broker;

public interface IFormalConnections
{
    bool SupportsFormal {get;}
    string FormalCapabilityReason=>"OWNER_CONFIGURED_ENDPOINTS_ONLY";
    JsonElement OpenConnection(string caller,Guid operation,ConnectionOpenRequest request);
    JsonElement GetConnectionStatus(string caller,Guid reference);
    JsonElement AttachConnection(string caller,Guid operation,Guid reference);
    JsonElement CloseConnection(string caller,Guid operation,Guid reference);
}
