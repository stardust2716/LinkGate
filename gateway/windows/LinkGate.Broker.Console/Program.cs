using LinkGate.Broker;

if (args.Length is not (2 or 3) || !Guid.TryParseExact(args[1], "N", out _) || (args.Length==3 && args[2]!="--pending-grants"))
{ Console.Error.WriteLine("Usage: LinkGate.Broker.Console <private-state-directory> <test-suffix> [--pending-grants]"); return 2; }
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    var store = new RegistrationStore(args[0]);
    using var host = new PipeHost(args[1], store,enablePendingGrants:args.Length==3);
    Task serving = host.RunAsync(stop.Token);
    Console.WriteLine(host.PipeName);
    Task<string?> input = Console.In.ReadLineAsync(stop.Token).AsTask();
    try { await Task.WhenAny(serving, input); }
    finally { stop.Cancel(); }
    try { await serving; } catch (OperationCanceledException) { }
    return 0;
}
catch (Exception) { Console.Error.WriteLine("Development Broker failed."); return 1; }
