using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Reflection;

internal static class RegistrationTests
{
    private static readonly Guid RestartOperation = Guid.Parse("00000000-0000-4000-8000-000000000002");
    private const string Manifest = """
        {"manifestVersion":1,"applicationId":"test.files","displayName":"Files","exports":[{"serviceId":"files","label":"Files","protocol":"http","localTarget":{"host":"127.0.0.1","port":18481}}],"imports":[]}
        """;

    public static async Task<bool> RunChildAsync(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--registration-replay" or "--registration-crash")) return false;
        var store = new RegistrationStore(args[1]);
        if (args[0] == "--registration-replay")
            Console.WriteLine(store.Register("restart-probe", RestartOperation, ManifestContract.Parse(Encoding.UTF8.GetBytes(Manifest))).GetRawText());
        else
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(args[1], "registration.db"), Pooling = false }.ToString());
            connection.Open();
            using var tx = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT INTO applications(caller,app,reference,manifest) VALUES('crash','crash','crash','{}')";
            command.ExecuteNonQuery();
            Console.WriteLine("transaction-open");
            await Task.Delay(TimeSpan.FromSeconds(20));
        }
        return true;
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        string repo = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(repo, "gateway"))) repo = Directory.GetParent(repo)?.FullName ?? throw new Exception("Repository not found");
        string root = Path.GetFullPath(Path.Combine(repo, "artifacts", "linkgate-registration-tests"));
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var manifest = ManifestContract.Parse(Encoding.UTF8.GetBytes(Manifest));
        try
        {
            var store = new RegistrationStore(directory, 2, 4, 2);
            Guid op = Guid.NewGuid();
            var first = store.Register("caller-a", op, manifest);
            check(first.GetProperty("approvalRequired").GetBoolean(), "pending only");
            check(first.GetProperty("permissionDiff").GetProperty("addedExports")[0].GetString() == "files", "export delta");
            check(first.GetRawText() == store.Register("caller-a", op, manifest).GetRawText(), "retry original result");
            var restart = new RegistrationStore(directory, 2, 4, 2);
            check(first.GetRawText() == restart.Register("caller-a", op, manifest).GetRawText(), "restart retry");
            check(restart.Status("caller-a", op).GetProperty("status").GetString() == "succeeded", "operation query");
            Expect("OPERATION_UNKNOWN", () => restart.Status("caller-b", op), check);
            var changed = ManifestContract.Parse(Encoding.UTF8.GetBytes(Manifest.Replace("18481", "18482")));
            Expect("OPERATION_CONFLICT", () => restart.Register("caller-a", op, changed), check);
            var update = restart.Register("caller-a", Guid.NewGuid(), changed);
            check(update.GetProperty("applicationRef").GetString() == first.GetProperty("applicationRef").GetString(), "stable application ref");
            check(update.GetProperty("permissionDiff").GetProperty("changedExports")[0].GetString() == "files", "changed target requires review");
            Expect("RESOURCE_EXHAUSTED", () => restart.Register("caller-a", Guid.NewGuid(), manifest), check);
            check(first.GetRawText() == restart.Register("caller-a", op, manifest).GetRawText(), "old retry survives capacity");
            var other = restart.Register("caller-b", op, manifest);
            check(other.GetProperty("applicationRef").GetString() != first.GetProperty("applicationRef").GetString(), "caller namespaces separated");
            Expect("RESOURCE_EXHAUSTED", () => restart.Register("caller-c", Guid.NewGuid(), manifest), check);
            var global = new RegistrationStore(Path.Combine(directory, "global"), 3, 1, 10);
            global.Register("caller-a", Guid.NewGuid(), manifest);
            Expect("RESOURCE_EXHAUSTED", () => global.Register("caller-b", Guid.NewGuid(), manifest), check);

            string atomic = Path.Combine(directory, "atomic");
            var failing = new RegistrationStore(atomic);
            Sql(atomic, "CREATE TRIGGER fail_operation BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-test-marker'); END;");
            try { failing.Register("caller", Guid.NewGuid(), manifest); throw new Exception("Write fault ignored"); }
            catch (SqliteException) { check(Count(atomic, "applications") == 0 && Count(atomic, "operations") == 0, "failed operation rolls back manifest"); }
            Sql(atomic, "DROP TRIGGER fail_operation");
            check(failing.Register("caller", Guid.NewGuid(), manifest).GetProperty("approvalRequired").GetBoolean(), "storage recovery");

            string parallel = Path.Combine(directory, "parallel");
            var one = new RegistrationStore(parallel); var two = new RegistrationStore(parallel);
            Guid sameOp = Guid.NewGuid();
            var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => (i % 2 == 0 ? one : two).Register("caller", sameOp, manifest).GetRawText())));
            check(results.Distinct().Count() == 1 && Count(parallel, "operations") == 1 && Count(parallel, "applications") == 1, "concurrent retry transaction");

            Sql(parallel, "PRAGMA user_version=99");
            try { _ = new RegistrationStore(parallel); throw new Exception("Unknown schema accepted"); }
            catch (InvalidDataException) { check(true, "unknown schema fails closed"); }

            string processState = Path.Combine(directory, "process");
            var beforeRestart = new RegistrationStore(processState);
            string original = beforeRestart.Register("restart-probe", RestartOperation, manifest).GetRawText();
            await Child("--registration-replay", processState, original, check);
            string crashState = Path.Combine(directory, "crash");
            await Child("--registration-crash", crashState, "transaction-open", check);
            _ = new RegistrationStore(crashState);
            check(Count(crashState, "applications") == 0 && Count(crashState, "operations") == 0, "process death rolls back transaction");

            await PipeRegistration(Path.Combine(directory, "pipe"), manifest, check);
        }
        finally
        {
            string target = Path.GetFullPath(directory);
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe cleanup path");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    private static async Task PipeRegistration(string directory, AppManifest manifest, Action<bool, string> check)
    {
        var store = new RegistrationStore(directory);
        var direct = new ControlProtocol(store);
        Guid operation = Guid.NewGuid();
        byte[] Request(string method, object parameters, Guid? op = null) => JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object> { ["kind"] = "request", ["apiVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString(),
                ["method"] = method, ["params"] = parameters }.Concat(op is null ? [] : new[] { new KeyValuePair<string, object>("operationId", op.Value.ToString()) }).ToDictionary());
        using var parsed = JsonDocument.Parse(manifest.ToJson());
        byte[] request = Request("app.register", new { manifest = parsed.RootElement }, operation);
        using (var unauth = JsonDocument.Parse(direct.Dispatch(request)))
            check(unauth.RootElement.GetProperty("error").GetProperty("code").GetString() == "AUTH_REQUIRED", "no request-supplied identity");
        using var host = new PipeHost(Guid.NewGuid().ToString("N"), store);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task running = host.RunAsync(stop.Token);
        async Task<JsonElement> Call(byte[] bytes)
        {
            using var client = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Impersonation);
            await client.ConnectAsync(stop.Token);
            await FrameCodec.WriteAsync(client, bytes, stop.Token);
            using var response = JsonDocument.Parse((await FrameCodec.ReadAsync(client, stop.Token))!);
            return response.RootElement.Clone();
        }
        try
        {
            var registered = await Call(request);
            check(registered.GetProperty("result").GetProperty("approvalRequired").GetBoolean(), "native registered pending");
            var retry = await Call(Request("app.register", new { manifest = parsed.RootElement }, operation));
            check(retry.GetProperty("result").GetRawText() == registered.GetProperty("result").GetRawText(), "RPC request id not in digest");
            var status = await Call(Request("operation.status", new { operationId = operation.ToString() }));
            check(status.GetProperty("result").GetProperty("result").GetRawText() == registered.GetProperty("result").GetRawText(), "native operation query");
            foreach (byte[] invalid in new[] {
                Request("app.register", new { manifest = parsed.RootElement }),
                Request("app.register", new { manifest = parsed.RootElement, caller = "fake" }, Guid.NewGuid()),
                Request("app.register", new { manifest = new { applicationId = "invalid" } }, Guid.NewGuid()),
                Request("operation.status", new { operationId = "not-uuid" }) })
                check((await Call(invalid)).GetProperty("error").GetProperty("code").GetString() == "INVALID_REQUEST", "strict registration params");
            foreach (string forbidden in new[] { "service.publish", "connection.open", "grant.revoke", "gateway.stop" })
                check((await Call(Request(forbidden, new { }, Guid.NewGuid()))).GetProperty("error").GetProperty("code").GetString() == "CAPABILITY_UNSUPPORTED", "no implicit grant");
            Sql(directory, "CREATE TRIGGER fail_operation BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-test-marker'); END;");
            using var changed = JsonDocument.Parse(manifest.ToJson().Replace("18481", "18482"));
            var failure = await Call(Request("app.register", new { manifest = changed.RootElement }, Guid.NewGuid()));
            check(failure.GetProperty("error").GetProperty("code").GetString() == "STORAGE_UNAVAILABLE" && !failure.GetRawText().Contains("private-test-marker"), "sanitized storage failure");
            check(Count(directory, "operations") == 1, "RPC failure leaves journal intact");
            using (var verification = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "registration.db"), Pooling = false }.ToString()))
            {
                verification.Open(); using var query = verification.CreateCommand(); query.CommandText = "SELECT manifest FROM applications";
                check(((string)query.ExecuteScalar()!).Contains("18481"), "failed update preserves old declaration");
            }
            using (var malformed = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await malformed.ConnectAsync(stop.Token);
                await FrameCodec.WriteAsync(malformed, Encoding.UTF8.GetBytes("{bad-json}"), stop.Token);
                try { check(await malformed.ReadAsync(new byte[1], stop.Token) == 0, "malformed frame closed"); }
                catch (IOException) { check(true, "malformed frame closed"); }
            }
            check((await Call(Request("operation.status", new { operationId = operation.ToString() }))).GetProperty("result").GetProperty("status").GetString() == "succeeded", "host survives malformed JSON");
        }
        finally { stop.Cancel(); try { await running; } catch (OperationCanceledException) { } }
    }

    private static void Expect(string code, Action action, Action<bool, string> check)
    { try { action(); throw new Exception("Expected " + code); } catch (RegistrationException e) { check(e.Code == code, code); } }
    private static async Task Child(string mode, string directory, string expected, Action<bool, string> check)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(mode); start.ArgumentList.Add(directory);
        using var process = Process.Start(start) ?? throw new Exception("Child start failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            check(line == expected, "independent process " + mode);
            if (mode == "--registration-replay") { await process.WaitForExitAsync(timeout.Token); check(process.ExitCode == 0, "replay process exit"); }
        }
        finally { if (!process.HasExited) process.Kill(); if (!process.WaitForExit(3000)) throw new Exception("Child cleanup failed"); }
    }
    private static void Sql(string directory, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "registration.db"), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static long Count(string directory, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "registration.db"), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table; return (long)command.ExecuteScalar()!;
    }
}
