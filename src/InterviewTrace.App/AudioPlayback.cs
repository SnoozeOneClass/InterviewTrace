using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InterviewTrace.App;

internal sealed class AudioPlayback : IDisposable
{
    private readonly List<AudioFileReader> _readers = [];
    private WasapiPlayer? _player;
    private MMDevice? _device;

    public void Play(IEnumerable<string> paths, string? outputDeviceId)
    {
        Stop();
        try
        {
            foreach (string path in paths)
            {
                if (!File.Exists(path)) throw new FileNotFoundException("录音文件不存在，请重新定位文件夹。", path);
                _readers.Add(new AudioFileReader(path));
            }
            if (_readers.Count == 0) throw new InvalidOperationException("没有可回放的音频。");
            ISampleProvider provider = _readers.Count == 1 ? _readers[0]
                : new MixingSampleProvider(_readers.Select(r => new VolumeSampleProvider(r) { Volume = 0.5f })) { ReadFully = false };
            var builder = new WasapiPlayerBuilder();
            if (!string.IsNullOrWhiteSpace(outputDeviceId))
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDevice(outputDeviceId);
                builder.WithDevice(_device);
            }
            _player = builder.Build();
            _player.Init(provider.ToWaveProvider());
            _player.Play();
        }
        catch { Stop(); throw; }
    }

    public void Stop()
    {
        try { _player?.Stop(); } catch (Exception) { /* The selected playback device may already be gone. */ }
        finally
        {
            try { _player?.Dispose(); } catch (Exception) { }
            _player = null;
            foreach (var reader in _readers) { try { reader.Dispose(); } catch (IOException) { } }
            _readers.Clear();
            try { _device?.Dispose(); } catch (Exception) { }
            _device = null;
        }
    }

    public void Dispose() => Stop();
}
