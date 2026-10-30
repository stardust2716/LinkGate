using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkGate.Broker;
using LinkGate.Control;

try
{
    if(args.Length==0)return Usage();
    if(args[0].StartsWith("server-",StringComparison.Ordinal))return await ServerCommands.Run(args);
    switch(args[0])
    {
        case "owner-create" when args.Length==2:
            if(Directory.Exists(args[1])&&Directory.EnumerateFileSystemEntries(args[1]).Any())throw new IOException("Empty new owner directory required.");
            SecureIdentity.MakePrivate(args[1]);
            using(var root=SecureIdentity.CreateOwnerRoot())
            using(var key=root.GetECDsaPrivateKey()??throw new CryptographicException())
            {SecureIdentity.WriteNew(Path.Combine(args[1],"owner-key.pem"),key.ExportPkcs8PrivateKeyPem());SecureIdentity.WriteNew(Path.Combine(args[1],"owner-root.pem"),root.ExportCertificatePem());}
            Output(new{created=true});return 0;
        case "device-request" when args.Length==2:
            string csr=SecureIdentity.CreateDeviceRequest(args[1]);SecureIdentity.WriteNew(Path.Combine(args[1],"device-request.pem"),csr);Output(new{created=true});return 0;
        case "issue" when args.Length==4:
            string rootPath=Path.Combine(args[1],"owner-root.pem"),keyPath=Path.Combine(args[1],"owner-key.pem");
            SecureIdentity.CheckPrivateFile(rootPath);SecureIdentity.CheckPrivateFile(keyPath);
            using(var owner=X509Certificate2.CreateFromPem(Read(rootPath),Read(keyPath)))SecureIdentity.WriteNew(args[3],SecureIdentity.IssueDeviceCertificate(owner,Read(args[2])));
            Output(new{issued=true});return 0;
        case "device-install" when args.Length==4:
            string deviceKey=Path.Combine(args[1],"device-key.pem");SecureIdentity.CheckPrivateFile(deviceKey);
            string leafText=Read(args[2]),rootText=Read(args[3]);
            using(var leaf=X509Certificate2.CreateFromPem(leafText,Read(deviceKey)))
            using(var root=X509Certificate2.CreateFromPem(rootText))
            {SecureIdentity.ValidateDeviceCertificate(leaf,root);InstallPublic(Path.Combine(args[1],"device-cert.pem"),leaf.ExportCertificatePem());InstallPublic(Path.Combine(args[1],"owner-root.pem"),root.ExportCertificatePem());Output(new{deviceId=SecureIdentity.KeyId(leaf),installed=true});}
            return 0;
        case "trust" when args.Length==9:
            string peerPem=Read(args[3]);using(var peerCertificate=X509Certificate2.CreateFromPem(peerPem))
                if(SecureIdentity.KeyId(peerCertificate)!=args[7])throw new InvalidDataException("Peer key confirmation differs.");
            using(var identity=SecureIdentity.Load(args[2]))
            {var store=new RegistrationStore(args[1]);store.BindSecureIdentity(identity);Output(store.TrustSecurePeer(identity,peerPem,long.Parse(args[4]),Guid.ParseExact(args[8],"D"),args[5],int.Parse(args[6])));}
            return 0;
        case "trust-certificate" when args.Length==7:
            string accessPeerPem=Read(args[3]);using(var accessPeerCertificate=X509Certificate2.CreateFromPem(accessPeerPem))
                if(SecureIdentity.KeyId(accessPeerCertificate)!=args[5])throw new InvalidDataException("Peer key confirmation differs.");
            using(var accessOwner=SecureIdentity.Load(args[2]))
            {var accessStore=new RegistrationStore(args[1]);accessStore.BindSecureIdentity(accessOwner);Output(accessStore.TrustSecurePeer(accessOwner,accessPeerPem,long.Parse(args[4]),Guid.ParseExact(args[6],"D")));}
            return 0;
        case "cert-key" when args.Length==2:
            using(var certificate=X509Certificate2.CreateFromPem(Read(args[1])))Output(new{deviceId=SecureIdentity.KeyId(certificate)});return 0;
        case "app-list" when args.Length==2:
            Output(new RegistrationStore(args[1]).ListReviewReferences());return 0;
        case "app-show" when args.Length==3:
            var appReview=new RegistrationStore(args[1]).GetReview(Guid.ParseExact(args[2],"D"));string manifest=appReview.PendingManifest.ToJson();
            Output(new{appReview.ApplicationRef,appReview.CallerScope,appReview.PendingRevision,appReview.DecisionVersion,manifest,manifestSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant()});return 0;
        case "app-approve" when args.Length==7:
            var appStore=new RegistrationStore(args[1]);var selectedApp=appStore.GetReview(Guid.ParseExact(args[2],"D"));string manifestHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selectedApp.PendingManifest.ToJson()))).ToLowerInvariant();
            if(selectedApp.PendingRevision!=long.Parse(args[3])||selectedApp.DecisionVersion!=long.Parse(args[4])||manifestHash!=args[5])throw new InvalidDataException();
            Output(appStore.DecideReview(selectedApp,ApplicationReviewDecision.Approved,Guid.ParseExact(args[6],"D")));return 0;
        case "app-operation-status" when args.Length==3:
            Output(new RegistrationStore(args[1]).GetReviewOperation(Guid.ParseExact(args[2],"D")));return 0;
        case "access-requests" when args.Length is >=2 and <=4:
            Output(new RegistrationStore(args[1]).ListPendingGrantReferences(args.Length>=3?Guid.ParseExact(args[2],"D"):null,args.Length==4?int.Parse(args[3]):20));return 0;
        case "access-list" when args.Length is >=2 and <=4:
            Output(new RegistrationStore(args[1]).ListLocalAccessReferences(args.Length>=3?Guid.ParseExact(args[2],"D"):null,args.Length==4?int.Parse(args[3]):20));return 0;
        case "access-show" when args.Length==3:
            Output(new RegistrationStore(args[1]).GetLocalAccess(Guid.ParseExact(args[2],"D")));return 0;
        case "access-propose" when args.Length==4:
            var access=new RegistrationStore(args[1]).ProposeLocalAccess(Guid.ParseExact(args[2],"D"));SecureIdentity.WriteNew(args[3],JsonSerializer.Serialize(access,new JsonSerializerOptions{WriteIndented=true}));Output(access);return 0;
        case "access-approve" when args.Length==4:
            using(var document=ControlJson.ReadDocument(Encoding.UTF8.GetBytes(Read(args[2]))))
            {
                var selected=JsonSerializer.Deserialize<LocalAccessProposal>(document.RootElement,new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException();
                Output(new RegistrationStore(args[1]).ApproveLocalAccess(selected,Guid.ParseExact(args[3],"D")));
            }
            return 0;
        case "access-revoke" when args.Length==5:
            Output(new RegistrationStore(args[1]).RevokeLocalAccess(Guid.ParseExact(args[2],"D"),long.Parse(args[3]),Guid.ParseExact(args[4],"D")));return 0;
        case "grant-propose" when args.Length==7:
            var proposal=new RegistrationStore(args[1]).ProposeSecureGrant(args[2],args[3],Guid.ParseExact(args[4],"D"),args[5]);
            SecureIdentity.WriteNew(args[6],JsonSerializer.Serialize(proposal,new JsonSerializerOptions{WriteIndented=true}));Output(proposal);return 0;
        case "grant-approve" when args.Length==4:
            using(var document=ControlJson.ReadDocument(Encoding.UTF8.GetBytes(Read(args[2]))))
            {
                var selected=JsonSerializer.Deserialize<SecureGrantProposal>(document.RootElement,new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException();
                Output(new RegistrationStore(args[1]).ApproveSecureGrant(selected,Guid.ParseExact(args[3],"D")));
            }
            return 0;
        case "grant-revoke" when args.Length==5:
            Output(new RegistrationStore(args[1]).RevokeSecureGrant(Guid.ParseExact(args[2],"D"),long.Parse(args[3]),Guid.ParseExact(args[4],"D")));return 0;
        case "peer-revoke" when args.Length==5:
            Output(new RegistrationStore(args[1]).RevokeSecurePeer(args[2],long.Parse(args[3]),Guid.ParseExact(args[4],"D")));return 0;
        case "operation-status" when args.Length==3:
            Output(new RegistrationStore(args[1]).GetSecureAuthorityOperation(Guid.ParseExact(args[2],"D")));return 0;
        default:return Usage();
    }
}
catch(ApplicationReviewException e){Console.Error.WriteLine("Owner operation rejected: "+e.Code);return 1;}
catch(Exception){Console.Error.WriteLine("Owner operation failed; inspect the original operation before retrying a mutation.");return 1;}

static void Output(object value)=>Console.WriteLine(JsonSerializer.Serialize(value));
static string Read(string path)
{
    path=Path.GetFullPath(path);var info=new FileInfo(path);
    for(DirectoryInfo? item=info.Directory;item is not null;item=item.Parent)if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked input rejected.");
    if(!info.Exists||info.Length is <1 or >65536||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Invalid owner input.");
    using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);using var reader=new StreamReader(file,new UTF8Encoding(false,true));return reader.ReadToEnd();
}
static void InstallPublic(string path,string value)
{
    if(File.Exists(path)){SecureIdentity.CheckPrivateFile(path);if(Read(path)!=value)throw new IOException("Existing identity differs.");}
    else SecureIdentity.WriteNew(path,value);
}
static int Usage()
{
    Console.Error.WriteLine("Owner commands:\n cert-key <certificate-file>\n app-list <state>\n app-show <state> <app-ref>\n app-approve <state> <app-ref> <pending-revision> <decision-version> <confirmed-manifest-SHA256> <operation-UUID>\n app-operation-status <state> <original-operation-UUID>\n owner-create <new-private-dir>\n device-request <new-device-dir>\n issue <owner-dir> <CSR-file> <new-certificate-file>\n device-install <device-dir> <certificate-file> <trusted-public-root-file>\n trust <state> <identity> <peer-certificate> <expected-revision> <IPv4> <port> <confirmed-peer-key> <operation-UUID>\n trust-certificate <state> <identity> <peer-certificate> <expected-revision> <confirmed-peer-key> <operation-UUID>\n grant-propose <state> <peer-key> <client-app> <publisher-ref> <service> <new-proposal-file>\n grant-approve <state> <proposal-file> <operation-UUID>\n grant-revoke <state> <grant-UUID> <expected-revision> <operation-UUID>\n peer-revoke <state> <peer-key> <expected-revision> <operation-UUID>\n access-requests <state> [after-request-UUID] [limit]\n access-list <state> [after-grant-UUID] [limit]\n access-show <state> <grant-UUID>\n access-propose <state> <request-UUID> <new-proposal-file>\n access-approve <state> <proposal-file> <operation-UUID>\n access-revoke <state> <grant-UUID> <expected-revision> <operation-UUID>\n operation-status <state> <original-operation-UUID>");return 2;
}
