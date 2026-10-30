using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LinkGate.Client;
using LinkGate.Contracts;

if (args.Length != 6 || args[1].Length != 64 || !args[1].All(Uri.IsHexDigit) || !Guid.TryParseExact(args[4], "D", out var operation) || args[5] is not ("register" or "query"))
{ Console.Error.WriteLine("Usage: Registration.Sample <trusted-broker-exe> <trusted-sha256> <private-state-directory> <manifest-path> <persisted-operation-uuid> <register|query>"); return 2; }
Process? owned = null;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
try
{
    string executable = Path.GetFullPath(args[0]);
    // Keep the verified file open, denying writes/deletion while creating the process.
    using var binary = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (!Convert.ToHexString(SHA256.HashData(binary)).Equals(args[1], StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException();
    var manifest = args[5] == "register" ? ManifestContract.Parse(File.ReadAllBytes(args[3])) : null;
    var start = new ProcessStartInfo(executable)
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
    start.ArgumentList.Add(Path.GetFullPath(args[2])); start.ArgumentList.Add(Guid.NewGuid().ToString("N"));
    owned = Process.Start(start) ?? throw new InvalidOperationException();
    string pipeName = await owned.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException();
    using var client = await VerifiedPipeClient.ConnectAsync(pipeName, owned.SafeHandle, timeout.Token);
    await client.HelloAsync(timeout.Token);
    JsonElement? registered = args[5] == "register"
        ? await client.RegisterPendingAsync(manifest!, operation, allowExperimentalPending: true, cancellationToken: timeout.Token)
        : null;
    var status = await client.GetOperationStatusAsync(operation, timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { registered, operation = status }));
    return 0;
}
catch (MutationOutcomeUnknownException e)
{ Console.Error.WriteLine($"Outcome unknown. Preserve operation {e.OperationId:D} and rerun in query mode after explicitly reconnecting."); return 3; }
catch (BrokerRequestException e) { Console.Error.WriteLine($"Broker rejected request: {e.Code}."); return 1; }
catch (Exception) { Console.Error.WriteLine("Registration sample failed."); return 1; }
finally
{
    if (owned is not null)
    {
        if (!owned.HasExited)
        {
            try { await owned.StandardInput.WriteLineAsync("stop"); } catch (IOException) { }
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await owned.WaitForExitAsync(shutdown.Token); }
            catch (OperationCanceledException) { if (!owned.HasExited) owned.Kill(); }
        }
        if (!owned.WaitForExit(3000)) throw new InvalidOperationException("Owned Broker did not exit.");
        owned.Dispose();
    }
}
