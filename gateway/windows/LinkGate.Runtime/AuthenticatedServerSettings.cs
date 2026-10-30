using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Control;

namespace LinkGate.Runtime;
public sealed class AuthenticatedServerSettings : IDisposable
{
    public IPAddress Address{get;}public int ControlPort{get;}public int BackendPort{get;}public X509Certificate2 Certificate{get;}public string ServerKey{get;}public IReadOnlyList<IPEndPoint> StunEndpoints{get;}
    private AuthenticatedServerSettings(IPAddress address,int control,int backend,X509Certificate2 certificate,string key,IPEndPoint[] stun){Address=address;ControlPort=control;BackendPort=backend;Certificate=certificate;ServerKey=key;StunEndpoints=Array.AsReadOnly(stun);}
    public static AuthenticatedServerSettings Parse(byte[] input,X509Certificate2 ownerRoot,string confirmedKey)
    {
        X509Certificate2? certificate=null;try
        {
            if(confirmedKey.Length!=64||confirmedKey.Any(x=>!(x is >= '0' and <= '9' or >= 'a' and <= 'f')))throw new InvalidDataException();using var document=ControlJson.ReadDocument(input);var v=document.RootElement;
            string[] required=["schema","host","controlPort","backendPort","serverCertificate"];if(v.ValueKind!=JsonValueKind.Object||required.Any(x=>!v.TryGetProperty(x,out _))||v.EnumerateObject().Any(x=>!required.Contains(x.Name)&&x.Name!="stunEndpoints")||v.GetProperty("schema").GetRawText()!="1")throw new InvalidDataException();
            string host=v.GetProperty("host").GetString()??"";if(!IPAddress.TryParse(host,out var address)||address.AddressFamily!=AddressFamily.InterNetwork||address.ToString()!=host||!Valid(address))throw new InvalidDataException();
            int Port(string name){var value=v.GetProperty(name);if(!value.TryGetInt32(out int port)||port is <1 or >65535||value.GetRawText()!=port.ToString(System.Globalization.CultureInfo.InvariantCulture))throw new InvalidDataException();return port;}
            int control=Port("controlPort"),backend=Port("backendPort");if(control==backend)throw new InvalidDataException();string pem=v.GetProperty("serverCertificate").GetString()??"";if(pem.Contains("PRIVATE KEY",StringComparison.Ordinal))throw new InvalidDataException();certificate=X509Certificate2.CreateFromPem(pem);SecureIdentity.ValidateDeviceCertificate(certificate,ownerRoot);string key=Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();if(key!=confirmedKey)throw new InvalidDataException();
            var stun=new List<IPEndPoint>();if(v.TryGetProperty("stunEndpoints",out var endpoints)){if(endpoints.ValueKind!=JsonValueKind.Array||endpoints.GetArrayLength()>4)throw new InvalidDataException();foreach(var endpoint in endpoints.EnumerateArray()){string text=endpoint.GetString()??"";if(!IPEndPoint.TryParse(text,out var parsed)||parsed.AddressFamily!=AddressFamily.InterNetwork||parsed.Port is <1 or >65535||parsed.ToString()!=text||!Valid(parsed.Address)||stun.Any(x=>x.Equals(parsed)))throw new InvalidDataException();stun.Add(parsed);}}
            var result=new AuthenticatedServerSettings(address,control,backend,certificate,key,stun.ToArray());certificate=null;return result;
        }catch(Exception error)when(error is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or CryptographicException or KeyNotFoundException){throw new InvalidDataException("Invalid confirmed server settings.");}finally{certificate?.Dispose();}
    }
    private static bool Valid(IPAddress address){var ip=address.GetAddressBytes();return !address.Equals(IPAddress.Any)&&!address.Equals(IPAddress.Broadcast)&&!(ip[0] is >=224 and <=239)&&!(ip[0]==198&&ip[1] is 18 or 19)&&!(ip[0]==169&&ip[1]==254);}
    public void Dispose()=>Certificate.Dispose();
}
