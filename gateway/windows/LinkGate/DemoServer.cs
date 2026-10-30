using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace LinkGate;

public static class DemoServer
{
    public static async Task RunAsync(int port, CancellationToken cancellation)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(16);
        using var slots = new SemaphoreSlim(16);
        Program.Emit(new { ok = true, demo = "listening", address = "127.0.0.1", port });
        var pending = new List<Task>();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation);
                if (!await slots.WaitAsync(0, cancellation)) { client.Dispose(); continue; }
                pending.RemoveAll(t => t.IsCompleted);
                pending.Add(ServeAsync(client, slots, cancellation));
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); await Task.WhenAll(pending); }
    }

    private static async Task ServeAsync(TcpClient client, SemaphoreSlim slots, CancellationToken cancellation)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var stream = client.GetStream();
                var header = new byte[8192];
                var length = 0;
                while (length < header.Length)
                {
                    var n = await stream.ReadAsync(header.AsMemory(length, 1), deadline.Token);
                    if (n == 0) return;
                    length += n;
                    if (length >= 4 && header[length - 4] == 13 && header[length - 3] == 10 && header[length - 2] == 13 && header[length - 1] == 10) break;
                }
                if (length >= header.Length) return;
                var rows = Encoding.ASCII.GetString(header, 0, length).Split("\r\n");
                var request = rows[0].Split(' ');
                if (request.Length != 3) return;
                if (request[0] != "GET")
                {
                    // Drain a small declared body before closing so ordinary POST
                    // clients receive the rejection instead of a TCP reset.
                    var lengths = rows.Where(row => row.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (lengths.Length > 1 || rows.Any(row => row.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))) return;
                    if (lengths.Length == 1)
                    {
                        if (!int.TryParse(lengths[0].Split(':', 2)[1].Trim(), out var bodyLength) || bodyLength is < 0 or > 4096) return;
                        await stream.ReadExactlyAsync(new byte[bodyLength], deadline.Token);
                    }
                    await Reply(stream, 405, "text/plain", "GET only"u8.ToArray(), deadline.Token);
                    return;
                }
                var path = request[1];
                if (path is "/" or "/health")
                {
                    await Reply(stream, 200, "application/json", "{\"ok\":true,\"service\":\"linkgate-demo\"}"u8.ToArray(), deadline.Token);
                }
                else if (path.StartsWith("/bytes/", StringComparison.Ordinal) && int.TryParse(path[7..], out var size) && size is >= 0 and <= 8 * 1024 * 1024)
                {
                    var body = new byte[size];
                    for (var i = 0; i < body.Length; i++) body[i] = (byte)(i % 251);
                    await Reply(stream, 200, "application/octet-stream", body, deadline.Token);
                }
                else if (path == "/ws")
                {
                    var fields = rows.Skip(1).Where(r => r.Contains(':')).Select(r => r.Split(':', 2)).GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
                    if (!fields.TryGetValue("Sec-WebSocket-Key", out var keys) || keys.Length != 1 || !fields.TryGetValue("Upgrade", out var upgrades) || upgrades.Length != 1 || !upgrades[0][1].Trim().Equals("websocket", StringComparison.OrdinalIgnoreCase)) return;
                    var rawKey = keys[0][1].Trim();
                    if (Convert.FromBase64String(rawKey).Length != 16) return;
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(rawKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    deadline.CancelAfter(TimeSpan.FromSeconds(90)); // Allow the 60s test and normal close handshake.
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), deadline.Token);
                    using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(15));
                    var buffer = new byte[65536];
                    while (socket.State == WebSocketState.Open)
                    {
                        var received = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
                        if (received.MessageType == WebSocketMessageType.Close) { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token); break; }
                        await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, deadline.Token);
                    }
                }
                else await Reply(stream, 404, "text/plain", "not found"u8.ToArray(), deadline.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or FormatException or WebSocketException) { }
            finally { slots.Release(); }
        }
    }
    private static async Task Reply(NetworkStream stream, int status, string type, byte[] body, CancellationToken cancellation)
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, cancellation);
        await stream.WriteAsync(body, cancellation);
    }
}
