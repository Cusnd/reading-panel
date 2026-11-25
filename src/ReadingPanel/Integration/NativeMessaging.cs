using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace ReadingPanel.Integration;

public sealed record ReaderMessage(string Type, string? Id = null, string? Text = null, int[]? Offsets = null, double[]? Point = null)
{
    public bool Valid => Type switch {
        "settings" => true,
        "dismiss" or "blur" => Guid.TryParse(Id, out _),
        "lookup" => Guid.TryParse(Id, out _) && Text is { Length: > 0 and <= 8000 } &&
            !string.IsNullOrWhiteSpace(Text) && Offsets is [var l, var t, var r, var b] &&
            l <= 0 && t <= 0 && r >= 0 && b >= 0 && Offsets.All(v => v is >= -32768 and <= 32768),
        _ => false
    };
}

public static class NativeMessaging
{
    public const string Host = "app.readingpanel.host";
    public const string Origin = "chrome-extension://fbdpbndhoedgnnihbgnglmlohjiioeac/";
    public static readonly string Pipe = $"ReadingPanel-{WindowsIdentity.GetCurrent().User!.Value}-{Process.GetCurrentProcess().SessionId}{WindowsReader.DesktopSuffix()}";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Stream stream, object value, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > 131072) throw new InvalidDataException("Invalid message length.");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    internal static async Task ListenAsync(MiddleClickState middleClick, Func<Guid, ReaderMessage, Task> handle, Func<Guid, Task> disconnected, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await server.WaitForConnectionAsync(token); }
            catch { server.Dispose(); throw; }
            _ = ServeAsync(server, middleClick, handle, disconnected, token);
        }
    }

    internal static async Task ServeAsync(NamedPipeServerStream server, MiddleClickState middleClick, Func<Guid, ReaderMessage, Task> handle,
        Func<Guid, Task> disconnected, CancellationToken token)
    {
        Guid client = Guid.NewGuid();
        using (server)
        {
            try
            {
                Task? settingsChanged = null;
                Task<byte[]>? incoming = null;
                while (!token.IsCancellationRequested)
                {
                    if (settingsChanged == null || settingsChanged.IsCompleted)
                    {
                        var snapshot = middleClick.Observe();
                        settingsChanged = snapshot.Changed;
                        await WriteAsync(server, new { type = "ready", edition = Program.Edition, desktopLookup = true,
                            middleClickEnabled = snapshot.Enabled }, token);
                    }
                    incoming ??= ReadAsync(server, token);
                    if (await Task.WhenAny(incoming, settingsChanged) != incoming) continue;
                    var message = JsonSerializer.Deserialize<ReaderMessage>(await incoming, Json);
                    incoming = null;
                    if (message?.Valid == true) await handle(client, message);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or OperationCanceledException) { }
            finally { if (!token.IsCancellationRequested) await disconnected(client); }
        }
    }

    private static NamedPipeClientStream Client() => new(".", Pipe, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);

    public static async Task RelayAsync()
    {
        using var pipe = Client();
        try { await pipe.ConnectAsync(0); }
        catch (TimeoutException)
        {
            if (Mutex.TryOpenExisting(@"Local\" + Pipe, out var running)) running.Dispose();
            else Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--background") { UseShellExecute = false })?.Dispose();
            await pipe.ConnectAsync(8000);
        }
        // Native messaging uses the same length-prefixed frames as the local reader pipe.
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var toBrowser = Task.Run(() => pipe.CopyToAsync(output));
        var fromBrowser = Task.Run(() => input.CopyToAsync(pipe));
        await await Task.WhenAny(toBrowser, fromBrowser);
    }

    public static async Task ShowExistingAsync()
    {
        using var pipe = Client();
        await pipe.ConnectAsync(3500);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = JsonDocument.Parse(await ReadAsync(pipe, deadline.Token));
        // Earlier releases only shipped the offline dictionary and sent a bare ready frame.
        string? running = ready.RootElement.TryGetProperty("edition", out var edition) ? edition.GetString() : "Offline";
        if (running != Program.Edition)
            throw new InvalidOperationException($"Reading Panel {running} is running. Choose Exit from its tray icon, then open Reading Panel {Program.Edition}.");
        if (!ready.RootElement.TryGetProperty("desktopLookup", out _))
            throw new InvalidOperationException("An earlier Reading Panel is running. Choose Exit from its tray icon, then open this version for Windows-wide lookup.");
        await WriteAsync(pipe, new { type = "settings" }, CancellationToken.None);
    }

}
