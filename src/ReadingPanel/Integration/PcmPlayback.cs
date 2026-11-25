using NAudio.Wave;

namespace ReadingPanel.Integration;

// Keep the device open across gaps between network packets. Return EOF only after the producer finishes.
internal interface IPcmPlayback : IDisposable
{
    void Write(byte[] pcm);
    Task DrainAsync();
}

internal sealed class PcmPlayback(int rate, CancellationToken ct) : IPcmPlayback
{
    private readonly BufferedWaveProvider buffer = new(new WaveFormat(rate, 16, 1)) { BufferDuration = TimeSpan.FromSeconds(4), ReadFully = true };
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class NotifyingProvider(BufferedWaveProvider source, object gate) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(byte[] data, int offset, int count)
        {
            lock (gate)
            {
                int read = source.Read(data, offset, count);
                Monitor.Pulse(gate);
                return read;
            }
        }
    }

    private readonly object gate = new();
    private CancellationTokenRegistration cancellation;
    private void WakeWriter() { lock (gate) Monitor.Pulse(gate); }
    private WaveOutEvent? device;
    public void Write(byte[] pcm)
    {
        if (pcm.Length % 2 != 0) throw new InvalidDataException("朗读服务返回的 PCM 音频不完整。");
        int offset = 0;
        while (offset < pcm.Length)
        {
            lock (gate)
            {
                ct.ThrowIfCancellationRequested();
                if (stopped.Task.IsCompleted) { stopped.Task.GetAwaiter().GetResult(); throw new IOException("音频设备已停止。"); }
                int available = (buffer.BufferLength - buffer.BufferedBytes) & ~1;
                int count = Math.Min(available, pcm.Length - offset);
                if (count == 0) { Monitor.Wait(gate); continue; }
                buffer.AddSamples(pcm, offset, count);
                offset += count;
            }
            if (device == null)
            {
                device = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3 };
                cancellation = ct.Register(WakeWriter);
                device.PlaybackStopped += (_, e) => { if (e.Exception != null) stopped.TrySetException(e.Exception); else stopped.TrySetResult(); WakeWriter(); };
                device.Init(new NotifyingProvider(buffer, gate));
                device.Play();
            }
        }
    }
    public Task DrainAsync()
    {
        if (device == null) throw new InvalidDataException("朗读服务未返回音频。");
        lock (gate) buffer.ReadFully = false;
        return stopped.Task.WaitAsync(ct);
    }
    public void Dispose() { device?.Dispose(); cancellation.Dispose(); }
}
