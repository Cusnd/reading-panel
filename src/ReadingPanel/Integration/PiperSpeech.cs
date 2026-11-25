using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace ReadingPanel.Integration;

internal static class PiperPack
{
    internal const string Folder = "piper-john-v1", Model = "en_US-john-medium.onnx";
    private const string ModelBase = "https://huggingface.co/rhasspy/piper-voices/resolve/e9c8a39444359388adaa94255becb37a5a2b8e51/en/en_US/john/medium/";
    private static readonly (string Name, string Url, string Digest)[] Sources = [
        ("piper_windows_amd64.zip", "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip",
            "F3C58906402B24F3A96D92145F58ACBA6D86C9B5DB896D207F78DC80811EFCEA"),
        (Model, ModelBase + Model, "789C6C875726E627DDEE93D51D8727859ABE9C091C3D141591F4B83C2072E988"),
        (Model + ".json", ModelBase + Model + ".json", "AF60F177B6B550F3D7A302720C0FB89E7F94A82B5DCA464775EF63B1C69BA09A"),
        ("MODEL_CARD", ModelBase + "MODEL_CARD", "A056DA65CD70DCBDA96FC3420FE8D3442DBF3C8B75F49CDF800A715B42E299D1")
    ];

    internal static bool Ready(string path) => File.Exists(Path.Combine(path, "piper.exe")) && File.Exists(Path.Combine(path, Model))
        && File.Exists(Path.Combine(path, Model + ".json")) && File.Exists(Path.Combine(path, "espeak-ng-data", "phontab"));
    internal static string? Find() => new[] { Path.Combine(AppContext.BaseDirectory, Folder), Path.Combine(Program.DataDirectory, Folder) }.FirstOrDefault(Ready);

    internal static async Task InstallAsync(string destination, string? cache, CancellationToken ct)
    {
        if (Ready(destination)) return;
        string parent = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!).FullName;
        string stage = Directory.CreateTempSubdirectory("reading-panel-voice-").FullName;
        string unpack = Directory.CreateDirectory(Path.Combine(parent, ".john-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            foreach (var source in Sources)
            {
                ct.ThrowIfCancellationRequested();
                string file = Path.Combine(stage, source.Name);
                string? cached = cache == null ? null : Path.Combine(cache, source.Name);
                if (cached != null && File.Exists(cached)) File.Copy(cached, file);
                else
                {
                    using var response = await http.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    await using var output = File.Create(file);
                    await response.Content.CopyToAsync(output, ct);
                }
                await using (var stream = File.OpenRead(file))
                {
                    if (Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != source.Digest)
                        throw new InvalidDataException("John 语音文件校验失败，请重新下载。");
                }
                if (source.Name.EndsWith(".zip", StringComparison.Ordinal))
                    ZipFile.ExtractToDirectory(file, unpack);
                else File.Copy(file, Path.Combine(unpack, "piper", source.Name));
            }
            string voice = Path.Combine(unpack, "piper");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "licenses", "Piper-NOTICES.txt"), Path.Combine(voice, "NOTICES.txt"));
            if (!Ready(voice)) throw new InvalidDataException("John 语音包不完整。");
            ct.ThrowIfCancellationRequested();
            try { Directory.Move(voice, destination); }
            catch (IOException) when (Ready(destination)) { } // Another installer finished first.
        }
        finally { Directory.Delete(stage, true); Directory.Delete(unpack, true); }
    }
}

internal sealed class PiperSpeech : IDisposable
{
    private readonly object gate = new();
    private readonly Timer idleTimer;
    private readonly TimeSpan idlePeriod;
    private readonly string? packDirectory;
    private long idleDeadline;
    private Process? worker;
    internal PiperSpeech(TimeSpan? idlePeriod = null, string? packDirectory = null)
    {
        this.packDirectory = packDirectory;
        this.idlePeriod = idlePeriod ?? TimeSpan.FromMinutes(3);
        idleTimer = new Timer(_ => {
            lock (gate)
            {
                if (idleDeadline == 0 || Environment.TickCount64 < idleDeadline) return;
                try { ReleasePack(); }
                catch (IOException) { idleTimer!.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan); }
            }
        });
    }

    internal static IEnumerable<string> Chunks(string text, int limit = 280)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 2);
        for (int start = 0; start < text.Length;)
        {
            int end = Math.Min(start + limit, text.Length);
            if (end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1])) end--;
            int pause = -1, space = -1, sentence = -1;
            for (int i = start; i < end; i++)
            {
                char c = text[i];
                if (c is '。' or '！' or '？' || (c is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]))))
                { sentence = i + 1; break; }
                if (char.IsWhiteSpace(c)) space = i + 1;
                if (c is ',' or ';' or ':' or '，' or '；' or '：') pause = i + 1;
            }
            if (sentence > start) end = sentence;
            else if (end < text.Length && pause > start) end = pause;
            else if (end < text.Length && space > start) end = space;
            var part = text[start..end].Trim();
            if (part.Length > 0) yield return part;
            start = end;
        }
    }

    private string? temporary;
    private static readonly UTF8Encoding Utf8 = new(false);
    internal async Task SpeakAsync(string input, Action<byte[]> audio, CancellationToken ct)
    {
        string folder = packDirectory ?? PiperPack.Find() ?? throw new InvalidOperationException("请在朗读设置中安装 Piper · John 语音。");
        lock (gate) { idleDeadline = 0; idleTimer.Change(Timeout.Infinite, Timeout.Infinite); }
        try
        {
            temporary ??= Directory.CreateTempSubdirectory("reading-panel-piper-").FullName;
            if (worker == null || worker.HasExited)
            {
                worker?.Dispose();
                var start = new ProcessStartInfo(Path.Combine(folder, "piper.exe")) {
                    WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8
                };
                foreach (string arg in new[] { "--model", PiperPack.Model, "--json-input", "--output_dir", temporary })
                    start.ArgumentList.Add(arg);
                worker = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Piper。");
                worker.BeginErrorReadLine();
            }
            var active = worker;
            using var stop = ct.Register(() => {
                try { if (!active.HasExited) active.Kill(); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            });
            byte[] pcm = new byte[16384];
            foreach (string part in Chunks(input))
            {
                ct.ThrowIfCancellationRequested();
                string wav = Path.Combine(temporary, Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { text = part, output_file = wav }).AsMemory(), ct);
                    await worker.StandardInput.FlushAsync(ct);
                    string? reply = await worker.StandardOutput.ReadLineAsync(ct);
                    if (!string.Equals(reply, wav, StringComparison.OrdinalIgnoreCase)) throw new IOException("Piper 未能生成音频。");
                    // Piper writes the reply just before its ofstream destructor closes the file.
                    using var reader = await OpenWaveAsync(wav, ct);
                    if (reader.WaveFormat.SampleRate != 22050 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1)
                        throw new InvalidDataException("Piper 音频格式异常。");
                    while (reader.Position < reader.Length)
                    {
                        ct.ThrowIfCancellationRequested();
                        int size = (int)Math.Min(pcm.Length, reader.Length - reader.Position);
                        reader.ReadExactly(pcm.AsSpan(0, size));
                        audio(size == pcm.Length ? pcm : pcm[..size]);
                    }
                }
                finally { try { File.Delete(wav); } catch (IOException) { } }
            }
        }
        catch { CloseWorker(); throw; }
        finally
        {
            lock (gate)
            {
                idleDeadline = Environment.TickCount64 + (long)idlePeriod.TotalMilliseconds;
                idleTimer.Change(idlePeriod, Timeout.InfiniteTimeSpan);
            }
        }
    }
    private static async Task<WaveFileReader> OpenWaveAsync(string file, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new WaveFileReader(file); }
            catch (IOException) when (attempt < 15) { await Task.Delay(10, ct); }
        }
    }
    private void CloseWorker()
    {
        if (worker == null) return;
        using var process = worker;
        worker = null;
        if (process.HasExited) return;
        try { process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited) { }
        process.WaitForExit(2000);
    }
    public void Dispose()
    {
        lock (gate) { idleTimer.Dispose(); idleDeadline = 0; ReleasePack(); }
    }

    private void ReleasePack()
    {
        CloseWorker();
        if (temporary == null) return;
        try { Directory.Delete(temporary, true); }
        catch (DirectoryNotFoundException) { }
        temporary = null;
    }
}
