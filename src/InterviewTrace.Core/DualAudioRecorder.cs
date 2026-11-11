using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace InterviewTrace.Core;

public sealed class DualAudioRecorder : IAsyncDisposable
{
    public static readonly WaveFormat Format = new(16000, 16, 1);
    private readonly CaptureTrack _microphone;
    private readonly CaptureTrack _system;
    private readonly long _started100ns;
    private int _stopping;
    private string? _failure;
    private string? _warning;

    public event Action<string>? Faulted;
    public event Action<string>? WarningRaised;
    public string? Failure => Volatile.Read(ref _failure);
    public string? Warning => Volatile.Read(ref _warning);
    public double MicrophoneLevel => _microphone.Level;
    public double SystemLevel => _system.Level;
    public TimeSpan Elapsed => TimeSpan.FromTicks(Math.Max(0, AlignedWaveWriter.Timestamp100ns() - _started100ns));

    private DualAudioRecorder(CaptureTrack microphone, CaptureTrack system, long started100ns)
    {
        _microphone = microphone;
        _system = system;
        _started100ns = started100ns;
        _microphone.Faulted += message => OnFault("麦克风", message);
        _system.Faulted += message => OnFault("系统声音", message);
        _microphone.WarningRaised += message => OnWarning("麦克风", message);
        _system.WarningRaised += message => OnWarning("系统声音", message);
    }

    private void OnWarning(string source, string message)
    {
        string warning = $"{source}：{message}";
        if (Interlocked.CompareExchange(ref _warning, warning, null) is null) WarningRaised?.Invoke(warning);
    }

    public static async Task<DualAudioRecorder> StartAsync(string microphoneId, string outputId,
        string microphonePath, string outputPath)
    {
        return await Task.Run(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? microphoneDevice = null;
            MMDevice? outputDevice = null;
            CaptureTrack? microphone = null;
            CaptureTrack? system = null;
            try
            {
                microphoneDevice = enumerator.GetDevice(microphoneId);
                outputDevice = enumerator.GetDevice(outputId);
                long start = AlignedWaveWriter.Timestamp100ns();
                microphone = new CaptureTrack(microphoneDevice, microphonePath, false, start);
                microphoneDevice = null; // ownership transferred
                system = new CaptureTrack(outputDevice, outputPath, true, start);
                outputDevice = null;
                var session = new DualAudioRecorder(microphone, system, start);
                microphone.Start();
                system.Start();
                return session;
            }
            catch
            {
                try { microphone?.Abort(); } catch { }
                try { system?.Abort(); } catch { }
                microphoneDevice?.Dispose();
                outputDevice?.Dispose();
                throw;
            }
        }).ConfigureAwait(false);
    }

    private void OnFault(string source, string message)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        string failure = $"{source}采集异常：{message}";
        if (Interlocked.CompareExchange(ref _failure, failure, null) is null) Faulted?.Invoke(failure);
    }

    public async Task<TimeSpan> StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return Elapsed;
        long end = AlignedWaveWriter.Timestamp100ns();
        await Task.WhenAll(_microphone.StopAsync(end), _system.StopAsync(end)).ConfigureAwait(false);
        return TimeSpan.FromTicks(Math.Max(0, end - _started100ns));
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed record Packet(byte[] Bytes, long Qpc100ns);

    private sealed class CaptureTrack
    {
        private readonly MMDevice _device;
        private readonly WasapiRecorder _recorder;
        private readonly AlignedWaveWriter _writer;
        private readonly Channel<Packet> _packets = Channel.CreateBounded<Packet>(new BoundedChannelOptions(300)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true
        });
        private readonly TaskCompletionSource<Exception?> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _writerTask;
        private readonly long _origin100ns;
        private int _stopping;
        private int _faulted;
        private int _started;
        private int _packetsReceived;
        private int _warningRaised;
        private long _peakTicks;
        private double _level;
        public event Action<string>? Faulted;
        public event Action<string>? WarningRaised;
        public double Level => Stopwatch.GetElapsedTime(Interlocked.Read(ref _peakTicks)) < TimeSpan.FromSeconds(1)
            ? Volatile.Read(ref _level) : 0;

        public CaptureTrack(MMDevice device, string path, bool loopback, long origin)
        {
            _device = device;
            _origin100ns = origin;
            var builder = new WasapiRecorderBuilder().WithDevice(device).WithFormat(Format).WithBufferLength(100);
            if (loopback) builder.WithLoopbackCapture();
            _recorder = builder.Build();
            try { _writer = new AlignedWaveWriter(path, Format, origin); }
            catch { _recorder.Dispose(); throw; }
            _recorder.DataAvailable += OnData;
            _recorder.RecordingStopped += (_, args) =>
            {
                _packets.Writer.TryComplete();
                _stopped.TrySetResult(args.Exception);
                if (Volatile.Read(ref _stopping) == 0)
                    ReportFault(args.Exception?.Message ?? "设备停止了采集。");
            };
            _writerTask = Task.Run(WritePacketsAsync);
        }

        public void Start()
        {
            _recorder.StartRecording();
            Volatile.Write(ref _started, 1);
        }

        private void OnData(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long devicePosition, long qpc)
        {
            if (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _faulted) != 0) return;
            int peak = 0;
            for (int i = 0; i + 1 < bytes.Length; i += 8)
                peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i, 2))));
            Volatile.Write(ref _level, peak / 32768.0);
            Interlocked.Exchange(ref _peakTicks, Stopwatch.GetTimestamp());
            int packetCount = Interlocked.Increment(ref _packetsReceived);
            if (packetCount > 1 && (flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
                ReportWarning("设备报告音频数据不连续，请在结束后回听检查。");
            long now = AlignedWaveWriter.Timestamp100ns();
            if (qpc <= 0 || qpc > now + TimeSpan.FromSeconds(5).Ticks ||
                qpc < _origin100ns - TimeSpan.FromSeconds(5).Ticks || (flags & AudioClientBufferFlags.TimestampError) != 0)
            {
                qpc = now;
                ReportWarning("部分音频时间戳异常，转写时间可能有偏差，请回听核对。");
            }
            if (!_packets.Writer.TryWrite(new Packet(bytes.ToArray(), qpc)))
                ReportFault("写入速度跟不上音频采集，录音已停止以避免静默丢失数据。");
        }

        private async Task WritePacketsAsync()
        {
            try
            {
                await foreach (Packet packet in _packets.Reader.ReadAllAsync().ConfigureAwait(false))
                    _writer.Append(packet.Bytes, packet.Qpc100ns);
            }
            catch (Exception ex)
            {
                _packets.Writer.TryComplete(ex);
                ReportFault($"写入音频失败：{ex.Message}");
                throw;
            }
        }

        private void ReportFault(string message)
        {
            if (Interlocked.Exchange(ref _faulted, 1) == 0) Faulted?.Invoke(message);
        }

        private void ReportWarning(string message)
        {
            if (Interlocked.Exchange(ref _warningRaised, 1) == 0) WarningRaised?.Invoke(message);
        }

        public async Task StopAsync(long end100ns)
        {
            Interlocked.Exchange(ref _stopping, 1);
            try
            {
                if (Volatile.Read(ref _started) != 0)
                {
                    _recorder.StopRecording();
                    await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                else _packets.Writer.TryComplete();
                await _writerTask.ConfigureAwait(false);
                _writer.Finish(end100ns);
            }
            finally
            {
                _packets.Writer.TryComplete();
                try { await _recorder.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    try { _writer.Dispose(); }
                    finally { _device.Dispose(); }
                }
            }
        }

        public void Abort()
        {
            Interlocked.Exchange(ref _stopping, 1);
            try { if (Volatile.Read(ref _started) != 0) _recorder.StopRecording(); }
            finally
            {
                _packets.Writer.TryComplete();
                try { _writerTask.GetAwaiter().GetResult(); } catch { }
                _recorder.Dispose();
                _writer.Dispose();
                _device.Dispose();
            }
        }
    }
}
