using System.Text;
using LinkGate.Broker;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

internal static class AuthorityEpochTests
{
    public static void Run(Action<bool,string> check)
    {
        string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException();
        string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-authority-epoch-tests")),directory=Path.Combine(root,Guid.NewGuid().ToString("N"));
        try
        {
            using var owner=SecureIdentity.CreateOwnerRoot();using var publisher=SecureStreamTests.Identity(Path.Combine(directory,"publisher"),owner);using var client=SecureStreamTests.Identity(Path.Combine(directory,"client"),owner);
            foreach(bool stale in new[]{false,true})
            {
                string state=Path.Combine(directory,stale?"stale":"valid");var store=new RegistrationStore(state);store.BindSecureIdentity(publisher);store.TrustSecurePeer(publisher,client.Certificate.ExportCertificatePem(),0,Guid.NewGuid());
                var manifest=ManifestContract.Parse(Encoding.UTF8.GetBytes("""{"manifestVersion":1,"applicationId":"org.example.epoch","displayName":"Epoch","exports":[{"serviceId":"file","label":"File","protocol":"tcp","localTarget":{"host":"127.0.0.1","port":18480}}],"imports":[]}"""));
                Guid app=store.Register("publisher",Guid.NewGuid(),manifest).GetProperty("applicationRef").GetGuid();Guid reviewed=Guid.NewGuid();var decision=store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,reviewed);store.ApproveSecureGrant(store.ProposeSecureGrant(client.DeviceId,"org.example.client",app,"file"),Guid.NewGuid());
                var scope=store.AuthorizeSecureOpen(client.DeviceId,"org.example.client","org.example.epoch","file","tcp",null);
                if(stale)store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Denied,Guid.NewGuid());
                using(var database=Open(state)){using var command=database.CreateCommand();command.CommandText="DROP TABLE connection_attachments; DROP TABLE connections; DROP INDEX secure_opens_active; DROP TABLE local_access; ALTER TABLE application_reviews DROP COLUMN authority_epoch; PRAGMA user_version=5";command.ExecuteNonQuery();}
                var migrated=new RegistrationStore(state);var review=migrated.GetReview(app);check(review.AuthorityEpoch==review.DecisionVersion&&migrated.GetReviewOperation(reviewed)==decision,"schema-five migration preserves decision outcomes and initializes the current authority epoch");
                check(migrated.SecureAuthorizationCurrent(scope,"org.example.client","org.example.epoch","file")==!stale,"schema-five migration preserves valid grants without reviving already stale stamps");
                var newGrant=migrated.ApproveSecureGrant(migrated.ProposeSecureGrant(client.DeviceId,"org.example.client",app,"file"),Guid.NewGuid());var live=migrated.AuthorizeSecureOpen(client.DeviceId,"org.example.client","org.example.epoch","file","tcp",null);
                var before=migrated.GetReview(app);migrated.DecideReview(before,ApplicationReviewDecision.Denied,Guid.NewGuid());check(migrated.GetReview(app).AuthorityEpoch==before.AuthorityEpoch&&migrated.SecureAuthorizationCurrent(live,"org.example.client","org.example.epoch","file"),"denial preserves the migrated live grant");
                migrated.DecideReview(migrated.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());check(!migrated.SecureAuthorizationCurrent(live,"org.example.client","org.example.epoch","file"),"explicit reapproval advances authority even for an identical declaration");
                using(var database=Open(state)){using var command=database.CreateCommand();command.CommandText="UPDATE application_reviews SET authority_epoch=-1";command.ExecuteNonQuery();}
                try{migrated.GetReview(app);throw new IOException("Corrupt epoch accepted.");}catch(ApplicationReviewException failure){check(failure.Code=="STORAGE_UNAVAILABLE","corrupt authority epoch fails closed");}
            }
        }
        finally{string target=Path.GetFullPath(directory);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe cleanup.");if(Directory.Exists(target))Directory.Delete(target,true);}
    }
    private static SqliteConnection Open(string state){var value=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(state,"registration.db"),Pooling=false}.ToString());value.Open();return value;}
}
