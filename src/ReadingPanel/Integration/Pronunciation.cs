using System.Net.NetworkInformation;
using System.Net.WebSockets;
using Google.Apis.Auth.OAuth2.Responses;
using Grpc.Core;
using ReadingPanel.Core;

namespace ReadingPanel.Integration;

internal sealed class Pronunciation(SpeechUsage usage, Func<int,CancellationToken,IPcmPlayback>? createOutput = null) : IDisposable
{
    private readonly GoogleSpeech google = new();
    private readonly RealtimeSpeech openAI = new();
    private readonly PiperSpeech piper = new();
    private readonly SemaphoreSlim engine = new(1);
    private readonly Dictionary<(SpeechMode, string),byte[]> cache = new();
    private readonly Queue<(SpeechMode, string)> oldest = new();
    private readonly Dictionary<SpeechMode, DateTimeOffset> retryAfter = new();
    // Completion can outlive the UI dispatcher during process shutdown.
    private sealed class PlaybackRequest : IDisposable
    {
        private readonly CancellationTokenSource source = new();
        private readonly object gate = new();
        private bool finished;
        internal CancellationToken Token => source.Token;
        internal void Cancel() { lock (gate) { if (!finished) source.Cancel(); } }
        public void Dispose() { lock (gate) { finished = true; source.Dispose(); } }
    }

    private PlaybackRequest? current;
    private Task? closing;
    public bool Playing => current != null;
    public string Voice { get; private set; } = "";
    public event Action? Changed;
    public event Action<string>? Failed;

    // Start, stop, and the completion callbacks run on the tray dispatcher.
    public void Toggle(string selection, SpeechOptions options)
    {
        if (closing != null) return;
        if (current != null) { Stop(); return; }
        string input = SpeechPolicy.Input(selection);
        if (input.Length == 0) return;
        current = new();
        Voice = "";
        Changed?.Invoke();
        _ = PlayAsync(current, input, options);
    }

    private async Task PlayAsync(PlaybackRequest request, string input, SpeechOptions options)
    {
        var dispatcher = SynchronizationContext.Current;
        var ct = request.Token;
        string? failure = null;
        try
        {
            await Task.Run(async () => {
                await engine.WaitAsync(ct);
                try
                {
                    long remaining = options.Mode is SpeechMode.Google or SpeechMode.Auto ? usage.Read(options.BillingTable).Remaining : 0;
                    var mode = SpeechPolicy.Resolve(options.Mode, remaining, SpeechPolicy.Characters(input), cache.ContainsKey((SpeechMode.Google, input)));
                    bool automatic = options.Mode == SpeechMode.Auto;
                    if (automatic && !cache.ContainsKey((mode, input)) && PiperPack.Find() != null
                        && (!NetworkInterface.GetIsNetworkAvailable() || retryAfter.GetValueOrDefault(mode) > usage.Now
                            || (mode == SpeechMode.OpenAI && options.RealtimeKey.Length == 0))) mode = SpeechMode.Piper;
                    bool emitted = false;
                    try { await PlayVoice(mode); }
                    catch (Exception error) when (automatic && !ct.IsCancellationRequested && !emitted && mode != SpeechMode.Piper
                        && PiperPack.Find() != null && IsConnectionError(error))
                    {
                        retryAfter[mode] = usage.Now.AddSeconds(30);
                        await PlayVoice(SpeechMode.Piper);
                    }
                    async Task PlayVoice(SpeechMode voice)
                    {
                        dispatcher?.Post(_ => { if (current == request) { Voice = SpeechPolicy.Label(voice); Changed?.Invoke(); } }, null);
                        int rate = voice == SpeechMode.Piper ? 22050 : 24000;
                        using var output = createOutput?.Invoke(rate, ct) ?? new PcmPlayback(rate, ct);
                        if (cache.TryGetValue((voice, input), out var saved)) output.Write(saved);
                        else
                        {
                            using var capture = new MemoryStream();
                            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            idle.CancelAfter(TimeSpan.FromSeconds(voice == SpeechMode.Google ? 8 : 25));
                            bool keep = true;
                            void Audio(byte[] data)
                            {
                                if (data.Length == 0) return;
                                ct.ThrowIfCancellationRequested();
                                emitted = true;
                                WriteAudio(output, data, idle, TimeSpan.FromSeconds(45));
                                if (capture.Length + data.Length > 1024 * 1024) keep = false;
                                if (keep) capture.Write(data);
                            }
                            switch (voice)
                            {
                                case SpeechMode.Google: await google.SpeakAsync(input, options, usage, Audio, idle.Token); break;
                                case SpeechMode.OpenAI: await openAI.SpeakAsync(input, options.RealtimeKey, usage, Audio, idle.Token); break;
                                case SpeechMode.Piper: await piper.SpeakAsync(input, Audio, idle.Token); break;
                            }
                            ct.ThrowIfCancellationRequested();
                            if (keep && capture.Length > 0)
                            {
                                if (oldest.Count == 12) cache.Remove(oldest.Dequeue());
                                cache[(voice, input)] = capture.ToArray();
                                oldest.Enqueue((voice, input));
                            }
                        }
                        await output.DrainAsync();
                    }
                }
                finally { engine.Release(); }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            failure = error switch {
                RpcException rpc => $"Google 朗读失败（{rpc.StatusCode}）。请检查网络、Google 凭据和项目权限。",
                TokenResponseException => "Google 凭据已失效，请重新登录应用默认凭据。",
                WebSocketException => "无法连接 OpenAI。请检查网络、API key 和项目权限。",
                OperationCanceledException => "等待语音服务超时，请重试或选择 Piper。",
                InvalidOperationException e when e.Message.Any(c => c >= 0x3400) => e.Message,
                InvalidOperationException => "请检查 Google 应用默认凭据和 Windows 音频输出。",
                _ => "无法完成朗读，请检查语音设置和 Windows 音频输出。"
            };
        }
        finally
        {
            request.Dispose();
            void Complete()
            {
                if (current != request) return;
                if (failure != null) Failed?.Invoke(failure);
                current = null;
                Changed?.Invoke();
            }
            if (dispatcher == null) Complete();
            else dispatcher.Post(_ => Complete(), null);
        }
    }

    internal static void WriteAudio(IPcmPlayback output, byte[] data, CancellationTokenSource idle, TimeSpan nextPacket)
    {
        idle.CancelAfter(Timeout.InfiniteTimeSpan);
        idle.Token.ThrowIfCancellationRequested();
        output.Write(data); // The device consumes PCM in real time. The user token still stops playback.
        idle.CancelAfter(nextPacket);
    }

    private static bool IsConnectionError(Exception error) => error is RpcException or WebSocketException or HttpRequestException
        or TokenResponseException or InvalidOperationException or OperationCanceledException;

    public void Stop()
    {
        var request = current;
        if (request == null) return;
        current = null;
        request.Cancel();
        Changed?.Invoke();
    }
    internal Task ShutdownAsync()
    {
        if (closing != null) return closing;
        Stop();
        return closing = Task.Run(async () => {
            await engine.WaitAsync().ConfigureAwait(false);
            try
            {
                try { piper.Dispose(); }
                finally
                {
                    try { google.Dispose(); }
                    finally { await openAI.CloseAsync().ConfigureAwait(false); }
                }
            }
            finally { engine.Release(); }
        }).WaitAsync(TimeSpan.FromSeconds(8));
    }

    public void Dispose() => ShutdownAsync().GetAwaiter().GetResult();
}
