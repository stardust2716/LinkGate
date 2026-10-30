using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

namespace LinkGate.Broker;

/// <summary>Private declaration, owner authority and secure-open journals; application registration alone activates no service.</summary>
public sealed partial class RegistrationStore
{
    private readonly string directory;
    private readonly int callerLimit, totalLimit, applicationLimit;
    private readonly object gate = new();
    public RegistrationStore(string directory) : this(directory, 500000, 1000000, 1024) { }

    internal RegistrationStore(string directory, int callerLimit, int totalLimit, int applicationLimit)
    {
        this.directory = Path.GetFullPath(directory);
        this.callerLimit = callerLimit; this.totalLimit = totalLimit; this.applicationLimit = applicationLimit;
        SecureDirectory();
        try { Initialize(); }
        catch (Exception e) when (e is SqliteException or RegistrationException)
        { throw new InvalidDataException("Registration storage unavailable."); }
    }

    private void Initialize()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        long version = Convert.ToInt64(Scalar(connection, transaction, "PRAGMA user_version"));
        if (version is <0 or >8) throw new InvalidDataException("Unsupported registration schema.");
        if (version == 0 && Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'")) != 0)
            throw new InvalidDataException("Unrecognized registration database.");
        if (version == 0) Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS applications(
                caller TEXT NOT NULL, app TEXT NOT NULL, reference TEXT NOT NULL,
                manifest TEXT NOT NULL, PRIMARY KEY(caller,app));
            CREATE TABLE IF NOT EXISTS operations(
                caller TEXT NOT NULL, operation TEXT NOT NULL, digest TEXT NOT NULL,
                result TEXT NOT NULL, PRIMARY KEY(caller,operation));
            """);
        if (version < 2)
        {
            Execute(connection, transaction, """
                CREATE TABLE application_reviews(
                    caller TEXT NOT NULL, app TEXT NOT NULL,
                    pending_revision INTEGER NOT NULL, decision_version INTEGER NOT NULL,
                    reviewed_revision INTEGER NOT NULL, decision TEXT NOT NULL,
                    approved_revision INTEGER, approved_manifest TEXT, PRIMARY KEY(caller,app));
                INSERT INTO application_reviews(caller,app,pending_revision,decision_version,reviewed_revision,decision)
                    SELECT caller,app,1,0,0,'None' FROM applications;
                """);
        }
        if (version < 4) Execute(connection, transaction, """
            CREATE TABLE grant_requests(
                reference TEXT NOT NULL PRIMARY KEY,caller TEXT NOT NULL,application TEXT NOT NULL,
                request TEXT NOT NULL,approved_revision INTEGER NOT NULL,decision_version INTEGER NOT NULL,import_json TEXT NOT NULL);
            CREATE INDEX grant_requests_caller ON grant_requests(caller);
            PRAGMA user_version=4;
            """);
        if(version<5)Execute(connection,transaction,"""
            CREATE TABLE secure_identity(singleton INTEGER PRIMARY KEY,device TEXT NOT NULL,root TEXT NOT NULL,max_utc INTEGER NOT NULL);
            CREATE TABLE secure_peers(peer TEXT PRIMARY KEY,certificate TEXT NOT NULL,revision INTEGER NOT NULL,active INTEGER NOT NULL,host TEXT,port INTEGER);
            CREATE TABLE secure_grants(reference TEXT PRIMARY KEY,peer TEXT NOT NULL,client TEXT NOT NULL,publisher TEXT NOT NULL,
                application TEXT NOT NULL,service TEXT NOT NULL,peer_revision INTEGER NOT NULL,publisher_revision INTEGER NOT NULL,
                decision_version INTEGER NOT NULL,revision INTEGER NOT NULL,active INTEGER NOT NULL,UNIQUE(peer,client,publisher,service));
            CREATE TABLE secure_opens(caller TEXT NOT NULL,operation TEXT NOT NULL,connection TEXT NOT NULL,boot TEXT NOT NULL,state TEXT NOT NULL,error TEXT,
                PRIMARY KEY(caller,operation));
            PRAGMA user_version=5;
            """);
        if(version<6)Execute(connection,transaction,"""
            ALTER TABLE application_reviews ADD COLUMN authority_epoch INTEGER NOT NULL DEFAULT 0;
            UPDATE application_reviews SET authority_epoch=decision_version;
            PRAGMA user_version=6;
            """);
        if(version<7)Execute(connection,transaction,"""
            CREATE TABLE local_access(reference TEXT PRIMARY KEY,caller TEXT NOT NULL,application TEXT NOT NULL,alias TEXT NOT NULL,
                peer TEXT NOT NULL,publisher TEXT NOT NULL,service TEXT NOT NULL,approved_revision INTEGER NOT NULL,
                authority_epoch INTEGER NOT NULL,peer_revision INTEGER NOT NULL,import_json TEXT NOT NULL,
                revision INTEGER NOT NULL,active INTEGER NOT NULL,UNIQUE(caller,application,alias,peer,publisher,service));
            CREATE INDEX local_access_peer ON local_access(peer,active);
            PRAGMA user_version=7;
            """);
        if(version<8)Execute(connection,transaction,"""
            CREATE TABLE connections(reference TEXT PRIMARY KEY,caller TEXT NOT NULL,operation TEXT NOT NULL,boot TEXT NOT NULL,
                request TEXT NOT NULL,scope TEXT NOT NULL,sequence INTEGER NOT NULL,terminal INTEGER NOT NULL,snapshot TEXT NOT NULL,
                UNIQUE(caller,operation));
            CREATE INDEX connections_active ON connections(caller) WHERE terminal=0;
            CREATE INDEX secure_opens_active ON secure_opens(caller) WHERE state!='closed';
            CREATE TABLE connection_attachments(reference TEXT PRIMARY KEY,caller TEXT NOT NULL,operation TEXT NOT NULL,
                stream TEXT NOT NULL UNIQUE,boot TEXT NOT NULL,consumed INTEGER NOT NULL);
            PRAGMA user_version=8;
            """);
        VerifyReviewSchema(connection, transaction);
        CheckExistingCapacity(connection, transaction);
        transaction.Commit();
    }

    internal JsonElement Register(string caller, Guid operation, AppManifest manifest)
    {
        string normalized = manifest.ToJson();
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("app.register\n" + normalized)));
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            VerifyReviewSchema(connection, transaction);
            using (var previous = Command(connection, transaction,
                "SELECT digest,result FROM operations WHERE caller=$caller AND operation=$op", ("$caller", caller), ("$op", operation.ToString())))
            using (var reader = previous.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != digest) throw new RegistrationException("OPERATION_CONFLICT");
                    return Parse(reader.GetString(1));
                }
            }
            AdmitRegistration(connection, transaction, caller);
            string? oldManifest = null, reference = null;
            using (var previousApplication = Command(connection, transaction, "SELECT reference,manifest FROM applications WHERE caller=$caller AND app=$app",
                ("$caller", caller), ("$app", manifest.ApplicationId)))
            using (var reader = previousApplication.ExecuteReader())
            { if (reader.Read()) { reference = reader.GetString(0); oldManifest = reader.GetString(1); } }
            if (reference is null && Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM applications")) >= applicationLimit)
                throw new RegistrationException("RESOURCE_EXHAUSTED");
            reference ??= Guid.NewGuid().ToString();
            AppManifest? old;
            try { old = oldManifest is null ? null : ManifestContract.Parse(Encoding.UTF8.GetBytes(oldManifest)); }
            catch (ContractException) { throw new RegistrationException("STORAGE_UNAVAILABLE"); }
            if (!Guid.TryParseExact(reference, "D", out _) || (old is not null && old.ApplicationId != manifest.ApplicationId))
                throw new RegistrationException("STORAGE_UNAVAILABLE");
            if (old is not null)
            {
                var review = ReadReview(connection, transaction, Guid.Parse(reference));
                if (review.Caller != caller || review.PendingManifest.ToJson() != oldManifest)
                    throw new RegistrationException("STORAGE_UNAVAILABLE");
                if (normalized != oldManifest)
                {
                    if (review.PendingRevision == long.MaxValue) throw new RegistrationException("RESOURCE_EXHAUSTED");
                    Execute(connection, transaction, "UPDATE application_reviews SET pending_revision=pending_revision+1 WHERE caller=$caller AND app=$app",
                        ("$caller", caller), ("$app", manifest.ApplicationId));
                }
            }
            string result = JsonSerializer.Serialize(new
            {
                applicationRef = reference,
                permissionDiff = new
                {
                    addedExports = manifest.Exports.Select(x => x.ServiceId).Except(old?.Exports.Select(x => x.ServiceId) ?? []).ToArray(),
                    removedExports = (old?.Exports.Select(x => x.ServiceId) ?? []).Except(manifest.Exports.Select(x => x.ServiceId)).ToArray(),
                    addedImports = manifest.Imports.Select(x => x.Alias).Except(old?.Imports.Select(x => x.Alias) ?? []).ToArray(),
                    removedImports = (old?.Imports.Select(x => x.Alias) ?? []).Except(manifest.Imports.Select(x => x.Alias)).ToArray(),
                    changedExports = Changed(oldManifest, normalized, "exports", "serviceId"),
                    changedImports = Changed(oldManifest, normalized, "imports", "alias"),
                    declarationChanged = oldManifest != normalized
                },
                approvalRequired = true
            });
            Execute(connection, transaction, """
                INSERT INTO applications(caller,app,reference,manifest) VALUES($caller,$app,$reference,$manifest)
                ON CONFLICT(caller,app) DO UPDATE SET manifest=excluded.manifest;
                """, ("$caller", caller), ("$app", manifest.ApplicationId), ("$reference", reference), ("$manifest", normalized));
            if (old is null) Execute(connection, transaction, """
                INSERT INTO application_reviews(caller,app,pending_revision,decision_version,reviewed_revision,decision)
                VALUES($caller,$app,1,0,0,'None')
                """, ("$caller", caller), ("$app", manifest.ApplicationId));
            Execute(connection, transaction, "INSERT INTO operations(caller,operation,digest,result) VALUES($caller,$op,$digest,$result)",
                ("$caller", caller), ("$op", operation.ToString()), ("$digest", digest), ("$result", result));
            transaction.Commit();
            return Parse(result);
        }
    }

    internal JsonElement Status(string caller, Guid operation)
    {
        lock (gate)
        {
            using var connection = Open();
            var formal=ReadConnectionOperation(connection,null,caller,operation);
            if(formal is not null)return formal.Value;
            using var command = Command(connection, null, "SELECT result FROM operations WHERE caller=$caller AND operation=$op",
                ("$caller", caller), ("$op", operation.ToString()));
            var result = command.ExecuteScalar() as string ?? throw new RegistrationException("OPERATION_UNKNOWN");
            return Parse(JsonSerializer.Serialize(new { operationId = operation.ToString(), status = "succeeded", result = Parse(result) }));
        }
    }

    private SqliteConnection Open()
    {
        RejectLinks();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "registration.db"), Pooling = false, DefaultTimeout = 1 }.ToString());
        try
        {
            connection.Open();
            Execute(connection, null, "PRAGMA synchronous=FULL; PRAGMA journal_mode=DELETE; PRAGMA max_page_count=262144;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private void SecureDirectory()
    {
        RejectLinks();
        var info = Directory.CreateDirectory(directory);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new UnauthorizedAccessException();
        var acl = new DirectorySecurity(); acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
        foreach (var subject in new[] { sid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(subject, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
        foreach (string file in new[] { "registration.db", "registration.db-journal", "registration.db-wal", "registration.db-shm" })
        {
            var path = new FileInfo(Path.Combine(directory, file));
            if (!path.Exists) continue;
            var fileAcl = new FileSecurity(); fileAcl.SetOwner(sid); fileAcl.SetAccessRuleProtection(true, false);
            foreach (var subject in new[] { sid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                fileAcl.AddAccessRule(new FileSystemAccessRule(subject, FileSystemRights.FullControl, AccessControlType.Allow));
            path.SetAccessControl(fileAcl);
        }
    }

    private void RejectLinks()
    {
        for (var item = new DirectoryInfo(directory); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked state path.");
        foreach (string file in new[] { "registration.db", "registration.db-journal", "registration.db-wal", "registration.db-shm" })
        {
            string path = Path.Combine(directory, file);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked state path.");
        }
    }

    private static JsonElement Parse(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        catch (JsonException) { throw new RegistrationException("STORAGE_UNAVAILABLE"); }
    }
    private static string[] Changed(string? previous, string current, string collection, string key)
    {
        if (previous is null) return [];
        using var old = JsonDocument.Parse(previous);
        using var next = JsonDocument.Parse(current);
        var prior = old.RootElement.GetProperty(collection).EnumerateArray().ToDictionary(x => x.GetProperty(key).GetString()!, x => x.GetRawText());
        return next.RootElement.GetProperty(collection).EnumerateArray()
            .Where(x => prior.TryGetValue(x.GetProperty(key).GetString()!, out string? value) && value != x.GetRawText())
            .Select(x => x.GetProperty(key).GetString()!).ToArray();
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object)[] values)
    {
        var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static object? Scalar(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object)[] values)
    { using var command = Command(c, t, sql, values); return command.ExecuteScalar(); }
    private static void Execute(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object)[] values)
    { using var command = Command(c, t, sql, values); command.ExecuteNonQuery(); }
}

internal sealed class RegistrationException(string code) : Exception("Registration rejected.")
{ internal string Code { get; } = code; }
