using System.Diagnostics;
using NAudio.Wave;

namespace InterviewTrace.Core;

/// <summary>Writes timestamped PCM packets onto one common capture timeline.</summary>
public sealed class AlignedWaveWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly WaveFileWriter _writer;
    private readonly WaveFormat _format;
    private readonly long _origin100ns;
    private long _lastFlushTicks;
    private bool _disposed;
    private readonly byte[] _silence = new byte[16 * 1024];
    private readonly long _toleranceFrames;

    public AlignedWaveWriter(string path, WaveFormat format, long origin100ns)
    {
        _format = format;
        _origin100ns = origin100ns;
        _toleranceFrames = Math.Max(1, format.SampleRate / 100); // 10 ms of timestamp jitter
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        _writer = new WaveFileWriter(_stream, format);
        _lastFlushTicks = Stopwatch.GetTimestamp();
        Flush();
    }

    public long FramesWritten => _writer.Length / _format.BlockAlign;

    public void Append(ReadOnlySpan<byte> pcm, long packetTime100ns)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcm.Length % _format.BlockAlign != 0) throw new InvalidDataException("音频数据不是完整采样帧。");
        long targetFrame = ToFrame(packetTime100ns);
        if (targetFrame < 0)
        {
            long skip = Math.Min(-targetFrame, pcm.Length / _format.BlockAlign);
            pcm = pcm[(int)(skip * _format.BlockAlign)..];
            targetFrame += skip;
        }
        long difference = targetFrame - FramesWritten;
        if (difference > _toleranceFrames) WriteSilence(difference);
        else if (difference < -_toleranceFrames)
        {
            long overlap = Math.Min(-difference, pcm.Length / _format.BlockAlign);
            pcm = pcm[(int)(overlap * _format.BlockAlign)..];
        }
        if (!pcm.IsEmpty) _writer.Write(pcm);
        if (Stopwatch.GetElapsedTime(_lastFlushTicks) >= TimeSpan.FromSeconds(5)) Flush();
    }

    public void Finish(long endTime100ns)
    {
        long endFrame = Math.Max(0, ToFrame(endTime100ns));
        if (endFrame > FramesWritten) WriteSilence(endFrame - FramesWritten);
        Flush();
    }

    public void Flush()
    {
        _writer.Flush(); // updates the WAV header so completed data is readable after an interruption
        _stream.Flush(flushToDisk: true);
        _lastFlushTicks = Stopwatch.GetTimestamp();
    }

    private long ToFrame(long time100ns) => (long)Math.Round(
        (time100ns - _origin100ns) * (_format.SampleRate / 10_000_000.0));

    private void WriteSilence(long frames)
    {
        long bytesRemaining = checked(frames * _format.BlockAlign);
        while (bytesRemaining > 0)
        {
            int count = (int)Math.Min(bytesRemaining, _silence.Length);
            count -= count % _format.BlockAlign;
            if (count == 0) throw new InvalidOperationException("不支持的音频帧大小。");
            _writer.Write(_silence.AsSpan(0, count));
            bytesRemaining -= count;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { _writer.Dispose(); }
        finally { _stream.Dispose(); _disposed = true; }
    }

    public static long Timestamp100ns() =>
        (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
}
