using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LinkGate.Control;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LinkGate.Authority;

public sealed class LeaseReceipt
{
    public Guid ID{get;}
    public string Issuer{get;}
    public Guid ServerEpoch{get;}
    public ulong Sequence{get;}
    public AuthoritySnapshot Authority{get;}
    private readonly LeaseClock clock;
    private readonly long began;
    private readonly TimeSpan lifetime;
    private readonly DateTimeOffset expires;
    private readonly object gate=new();private TimeSpan previousElapsed;private bool invalid;
    internal LeaseClock Clock=>clock;
    public TimeSpan Remaining { get { if(!Active)return TimeSpan.Zero;try{var reading=clock.Read();var mono=lifetime-clock.Source.GetElapsedTime(began,reading.Timestamp);var wall=expires-reading.Utc;return mono<wall?mono:wall;}catch(LeaseRejectedException){return TimeSpan.Zero;} } }
    internal LeaseReceipt(Guid id,string issuer,Guid epoch,ulong sequence,AuthoritySnapshot authority,LeaseClock clock,long began,TimeSpan lifetime,DateTimeOffset expires){ID=id;Issuer=issuer;ServerEpoch=epoch;Sequence=sequence;Authority=authority;this.clock=clock;this.began=began;this.lifetime=lifetime;this.expires=expires;}
    public bool Active{get{lock(gate){if(invalid)return false;try{var reading=clock.Read();TimeSpan elapsed=clock.Source.GetElapsedTime(began,reading.Timestamp);if(elapsed<TimeSpan.Zero||elapsed<previousElapsed||elapsed>=lifetime||reading.Utc>=expires){invalid=true;return false;}previousElapsed=elapsed;return true;}catch(Exception error)when(error is ArgumentException or LeaseRejectedException){invalid=true;return false;}}}}
}

public static class ServerLease
{
    public static async Task<LeaseReceipt> VerifyAsync(string token,X509Certificate2 issuerCertificate,X509Certificate2 ownerRoot,string expectedIssuer,Guid expectedEpoch,AuthoritySnapshot current,LeaseClock clock)
    {
        try {
            ArgumentNullException.ThrowIfNull(current);ArgumentNullException.ThrowIfNull(token);ArgumentNullException.ThrowIfNull(issuerCertificate);ArgumentNullException.ThrowIfNull(ownerRoot);
            ArgumentNullException.ThrowIfNull(clock);var reading=clock.Read();long began=reading.Timestamp;DateTimeOffset now=reading.Utc;LeaseJson.Key(expectedIssuer);ValidateCertificate(issuerCertificate,ownerRoot,now);
            string issuer=Convert.ToHexString(SHA256.HashData(issuerCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();if(issuer!=expectedIssuer||token.Length is <1 or >16384)throw new LeaseRejectedException();
            string[] parts=token.Split('.');if(parts.Length!=3||parts[0].Length>2048||parts[2].Length!=86)throw new LeaseRejectedException();byte[][] decoded=parts.Select(Decode).ToArray();if(decoded[2].Length!=64)throw new LeaseRejectedException();
            using var headerDocument=ControlJson.ReadDocument(decoded[0]);JsonElement header=headerDocument.RootElement;LeaseJson.Fields(header,"alg","typ","kid");if(LeaseJson.Text(header,"alg",5)!="ES256"||LeaseJson.Text(header,"typ",32)!="linkgate-lease+jwt"||LeaseJson.Text(header,"kid",64)!=issuer)throw new LeaseRejectedException();
            using var claimsDocument=ControlJson.ReadDocument(decoded[1]);JsonElement claims=claimsDocument.RootElement;LeaseJson.Fields(claims,"iss","sub","aud","jti","iat","nbf","exp","v","serverEpoch","sequence","scope","visitorRevision","publisherRevision","serviceRevision","grantRevision","protocol","authority");
            JsonElement audience=claims.GetProperty("aud");if(audience.ValueKind!=JsonValueKind.Array||audience.GetArrayLength()!=1||audience[0].GetString()!=current.Scope.Target.PublisherDevice||LeaseJson.Text(claims,"iss",64)!=issuer||LeaseJson.Text(claims,"sub",64)!=current.Scope.VisitorDevice||LeaseJson.Integer(claims,"v")!=1||LeaseJson.UUID(claims,"serverEpoch")!=expectedEpoch||LeaseJson.Scope(claims.GetProperty("scope"))!=current.Scope)throw new LeaseRejectedException();
            if(LeaseJson.Counter(claims,"visitorRevision")!=current.VisitorRevision||LeaseJson.Counter(claims,"publisherRevision")!=current.PublisherRevision||LeaseJson.Counter(claims,"serviceRevision")!=current.ServiceRevision||LeaseJson.Counter(claims,"grantRevision")!=current.GrantRevision||LeaseJson.Text(claims,"protocol",5)!=current.Protocol||LeaseJson.Text(claims,"authority",259,true)!=current.Authority)throw new LeaseRejectedException();
            Guid id=LeaseJson.UUID(claims,"jti");ulong sequence=LeaseJson.Counter(claims,"sequence",false);long issued=LeaseJson.Integer(claims,"iat"),notBefore=LeaseJson.Integer(claims,"nbf"),expires=LeaseJson.Integer(claims,"exp");if(issued>now.ToUnixTimeSeconds()||notBefore!=issued||expires<=issued||expires-issued>120||expires<=now.ToUnixTimeSeconds())throw new LeaseRejectedException();
            using var key=issuerCertificate.GetECDsaPublicKey()??throw new LeaseRejectedException();var signingKey=new ECDsaSecurityKey(key){KeyId=issuer,CryptoProviderFactory=new CryptoProviderFactory{CacheSignatureProviders=false}};
            var parameters=new TokenValidationParameters {RequireSignedTokens=true,RequireExpirationTime=true,ValidateIssuerSigningKey=true,IssuerSigningKey=signingKey,TryAllIssuerSigningKeys=false,ValidateIssuer=true,ValidIssuer=issuer,ValidateAudience=true,ValidAudience=current.Scope.Target.PublisherDevice,ValidateLifetime=true,ClockSkew=TimeSpan.Zero,ValidAlgorithms=[SecurityAlgorithms.EcdsaSha256],ValidTypes=["linkgate-lease+jwt"],IncludeTokenOnFailedValidation=false,LogTokenId=false,LogValidationExceptions=false,LifetimeValidator=(start,end,_,_)=>start.HasValue&&end.HasValue&&start.Value<=now.UtcDateTime&&end.Value>now.UtcDateTime};
            var handler=new JsonWebTokenHandler{MaximumTokenSizeInBytes=16384};var validated=await handler.ValidateTokenAsync(token,parameters);if(!validated.IsValid)throw new LeaseRejectedException();
            DateTimeOffset deadline=DateTimeOffset.FromUnixTimeSeconds(expires);foreach(DateTime candidate in new[]{issuerCertificate.NotAfter,ownerRoot.NotAfter}){var bound=new DateTimeOffset(candidate.ToUniversalTime());if(bound<deadline)deadline=bound;}
            TimeSpan remaining=deadline-now;if(remaining<=TimeSpan.Zero||remaining>TimeSpan.FromSeconds(120))throw new LeaseRejectedException();var receipt=new LeaseReceipt(id,issuer,expectedEpoch,sequence,current,clock,began,remaining,deadline);if(!receipt.Active)throw new LeaseRejectedException();return receipt;
        }catch(LeaseRejectedException){throw;}
        catch(Exception error)when(error is ArgumentException or InvalidOperationException or InvalidDataException or JsonException or CryptographicException or FormatException or OverflowException or KeyNotFoundException or SecurityTokenException){throw new LeaseRejectedException();}
    }
    private static byte[] Decode(string value){if(value.Length==0||value.Any(item=>!(item is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))throw new LeaseRejectedException();byte[] decoded=Base64UrlEncoder.DecodeBytes(value);if(Base64UrlEncoder.Encode(decoded)!=value)throw new LeaseRejectedException();return decoded;}
    internal static void ValidateCertificate(X509Certificate2 leaf,X509Certificate2 root,DateTimeOffset now)
    {
        bool exactName=leaf.Extensions.Cast<X509Extension>().Where(value=>value.Oid?.Value=="2.5.29.17").Any(value=>new X509SubjectAlternativeNameExtension(value.RawData,value.Critical).EnumerateDnsNames().Contains("linkgate.device",StringComparer.Ordinal));
        using var key=leaf.GetECDsaPublicKey();if(key is null||key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7"||leaf.Extensions.OfType<X509BasicConstraintsExtension>().Any(value=>value.CertificateAuthority)||!root.Extensions.OfType<X509BasicConstraintsExtension>().Any(value=>value.CertificateAuthority)||!exactName||now.UtcDateTime<root.NotBefore.ToUniversalTime()||now.UtcDateTime>=root.NotAfter.ToUniversalTime()||now.UtcDateTime<leaf.NotBefore.ToUniversalTime()||now.UtcDateTime>=leaf.NotAfter.ToUniversalTime())throw new LeaseRejectedException();
        var usages=leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SelectMany(value=>value.EnhancedKeyUsages.Cast<Oid>()).Select(value=>value.Value).ToHashSet(StringComparer.Ordinal);if(!usages.Contains("1.3.6.1.5.5.7.3.1")||!usages.Contains("1.3.6.1.5.5.7.3.2"))throw new LeaseRejectedException();
        using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(root);chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;chain.ChainPolicy.DisableCertificateDownloads=true;chain.ChainPolicy.VerificationTime=now.UtcDateTime;chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));if(!chain.Build(leaf)||chain.ChainElements.Count!=2||!chain.ChainElements[1].Certificate.RawData.AsSpan().SequenceEqual(root.RawData))throw new LeaseRejectedException();
    }
}

public sealed class LeaseWindow
{
    private readonly object gate=new();private LeaseReceipt? current;private bool closed;private LeaseClock? clock;
    private void Observe(LeaseReceipt receipt){if(clock is not null&&!ReferenceEquals(clock,receipt.Clock)){closed=true;throw new LeaseRejectedException();}clock=receipt.Clock;try{_=clock.Read();}catch(LeaseRejectedException){closed=true;throw;}}
    public LeaseReceipt Accept(LeaseReceipt receipt)
    {
        lock(gate){if(closed)throw new LeaseRejectedException();Observe(receipt);if(!receipt.Active)throw new LeaseRejectedException();if(current is not null){if(current.Authority.Scope!=receipt.Authority.Scope||current.Issuer!=receipt.Issuer||current.ServerEpoch!=receipt.ServerEpoch)throw new LeaseRejectedException();if(current.ID==receipt.ID){if(current.Sequence!=receipt.Sequence||!current.Active)throw new LeaseRejectedException();return current;}if(receipt.Sequence<=current.Sequence)throw new LeaseRejectedException();}current=receipt;return receipt;}
    }
    public LeaseReceipt Current{get{lock(gate){if(closed||current is null)throw new LeaseRejectedException();Observe(current);if(!current.Active)throw new LeaseRejectedException();return current;}}}
    public void Close(){lock(gate){closed=true;}}
}
