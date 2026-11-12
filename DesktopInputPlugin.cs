using System.Diagnostics;
using StreamUI.Contracts;

namespace StreamUI.DesktopInput;

public sealed class DesktopInputPlugin : IStreamUIPlugin
{
    private readonly InputState input = new();
    private RawInputCapture? capture;
    private CancellationTokenSource? stop;
    private Task? publisher;

    public async Task StartAsync(IPluginContext context, CancellationToken stopping)
    {
        capture = new(input);
        await capture.Started.WaitAsync(stopping);
        stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        publisher = PublishFrames(context, stop.Token);
    }

    private async Task PublishFrames(IPluginContext context, CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 60));
        var frame = new byte[InputState.FrameSize];
        var previous = new byte[InputState.FrameSize];
        var sampledAt = Stopwatch.GetTimestamp();
        long publishedAt = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stopping))
            {
                var timestamp = Stopwatch.GetTimestamp();
                var now = Environment.TickCount64;
                input.WriteFrame(frame, now, Stopwatch.GetElapsedTime(sampledAt, timestamp).TotalSeconds);
                sampledAt = timestamp;
                if (now - publishedAt < 200 && frame.AsSpan().SequenceEqual(previous)) continue;
                context.PublishBinary("frames", frame.ToArray());
                frame.CopyTo(previous, 0);
                publishedAt = now;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (stop is not null) await stop.CancelAsync();
        try
        {
            if (capture is not null) await capture.DisposeAsync();
        }
        finally
        {
            try { if (publisher is not null) await publisher; }
            finally { stop?.Dispose(); }
        }
    }
}
