using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LinkGate.Broker;

public sealed class SecureIdentity : IDisposable
{
    public X509Certificate2 Certificate{get;}
    public X509Certificate2 Root{get;}
    public string DeviceId=>KeyId(Certificate);
    public SecureIdentity(X509Certificate2 certificate,X509Certificate2 root)
    {if(!certificate.HasPrivateKey)throw new InvalidDataException("Private device identity required.");Certificate=certificate;Root=root;}
    public static string KeyId(X509Certificate2 certificate)=>Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    private X509ChainPolicy Policy(bool server)
    {
        var policy=new X509ChainPolicy{TrustMode=X509ChainTrustMode.CustomRootTrust,RevocationMode=X509RevocationMode.NoCheck,DisableCertificateDownloads=true};
        policy.CustomTrustStore.Add(Root);policy.ApplicationPolicy.Add(new Oid(server?"1.3.6.1.5.5.7.3.1":"1.3.6.1.5.5.7.3.2"));return policy;
    }
    public SslClientAuthenticationOptions ClientOptions(string expectedPeer)=>new()
    {
        TargetHost="linkgate.device",EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
        ClientCertificates=new X509CertificateCollection{Certificate},CertificateChainPolicy=Policy(true),
        RemoteCertificateValidationCallback=(_,certificate,_,errors)=>ValidatePeer(certificate,errors,key=>key==expectedPeer)
    };
    public SslServerAuthenticationOptions ServerOptions(Func<string,bool> allowed)=>new()
    {
        ServerCertificate=Certificate,ClientCertificateRequired=true,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,CertificateChainPolicy=Policy(false),
        RemoteCertificateValidationCallback=(_,certificate,_,errors)=>ValidatePeer(certificate,errors,allowed)
    };
    private static bool ValidatePeer(X509Certificate? certificate,SslPolicyErrors errors,Func<string,bool> allowed)
    {
        if(errors!=SslPolicyErrors.None || certificate is null)return false;
        try{using var value=new X509Certificate2(certificate);return allowed(KeyId(value));}catch(Exception){return false;}
    }
    public static void ValidateDeviceCertificate(X509Certificate2 certificate,X509Certificate2 root)
    {
        using var key=certificate.GetECDsaPublicKey();
        if(key is null || key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7" || certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e=>e.CertificateAuthority))throw new InvalidDataException("Invalid device certificate.");
        using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;chain.ChainPolicy.DisableCertificateDownloads=true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        if(!chain.Build(certificate) || certificate.GetNameInfo(X509NameType.DnsName,false)!="linkgate.device")throw new InvalidDataException("Invalid device certificate.");
    }
    public static X509Certificate2 CreateOwnerRoot()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var request=new CertificateRequest("CN=LinkGate owner",key,HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(1));
    }
    public static string CreateDeviceRequest(string privateDirectory)
    {
        if(Directory.Exists(privateDirectory)&&Directory.EnumerateFileSystemEntries(privateDirectory).Any())throw new IOException("New empty private identity directory required.");
        MakePrivate(privateDirectory);using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteNew(Path.Combine(privateDirectory,"device-key.pem"),key.ExportPkcs8PrivateKeyPem());
        return new CertificateRequest("CN=LinkGate device",key,HashAlgorithmName.SHA256).CreateSigningRequestPem();
    }
    public static string IssueDeviceCertificate(X509Certificate2 owner,string csr)
    {
        var request=CertificateRequest.LoadSigningRequestPem(csr,HashAlgorithmName.SHA256);
        using(var key=ECDsa.Create())
        {key.ImportSubjectPublicKeyInfo(request.PublicKey.ExportSubjectPublicKeyInfo(),out _);if(key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7")throw new InvalidDataException("P-256 request required.");}
        request.CertificateExtensions.Clear();request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1"),new("1.3.6.1.5.5.7.3.2")},true));
        var name=new SubjectAlternativeNameBuilder();name.AddDnsName("linkgate.device");request.CertificateExtensions.Add(name.Build());
        using var certificate=request.Create(owner,DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(30),RandomNumberGenerator.GetBytes(16));return certificate.ExportCertificatePem();
    }
    public static SecureIdentity Load(string privateDirectory)
    {
        foreach(string name in new[]{"device-cert.pem","device-key.pem","owner-root.pem"})CheckPrivateFile(Path.Combine(privateDirectory,name));
        using var pem=X509Certificate2.CreateFromPemFile(Path.Combine(privateDirectory,"device-cert.pem"),Path.Combine(privateDirectory,"device-key.pem"));
        // Windows Schannel receives a temporary user key handle; disposal removes this imported key.
        var certificate=X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.UserKeySet);
        var root=X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(privateDirectory,"owner-root.pem")));
        try{ValidateDeviceCertificate(certificate,root);return new SecureIdentity(certificate,root);}catch{certificate.Dispose();root.Dispose();throw;}
    }
    public static void CheckPrivateFile(string path)
    {
        for(var item=new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);item is not null;item=item.Parent)
            if(item.Exists&&(item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked private state rejected.");
        var file=new FileInfo(path);if(!file.Exists || file.Length is <1 or >65536 || (file.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Invalid private identity file.");
        using var current=WindowsIdentity.GetCurrent();var user=current.User??throw new UnauthorizedAccessException();var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        foreach(FileSystemAccessRule rule in file.GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&!rule.IdentityReference.Equals(user)&&!rule.IdentityReference.Equals(system))throw new UnauthorizedAccessException("Private identity ACL required.");
    }
    public static void MakePrivate(string directory)
    {
        for(var item=new DirectoryInfo(Path.GetFullPath(directory));item is not null;item=item.Parent)
            if(item.Exists&&(item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked private state rejected.");
        var info=Directory.CreateDirectory(directory);using var identity=WindowsIdentity.GetCurrent();var user=identity.User??throw new UnauthorizedAccessException();
        var acl=new DirectorySecurity();acl.SetOwner(user);acl.SetAccessRuleProtection(true,false);
        foreach(var sid in new[]{user,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        info.SetAccessControl(acl);
    }
    public static void WriteNew(string path,string text)
    {using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);file.Write(System.Text.Encoding.UTF8.GetBytes(text));file.Flush(true);}
    public void Dispose(){Certificate.Dispose();Root.Dispose();}
}
