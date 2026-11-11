using System.Buffers.Binary;
using InterviewTrace.Core;
using NAudio.Wave;

string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "smoke-output"));
Directory.CreateDirectory(parent);
string work = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
if (!work.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Invalid test output path.");
Directory.CreateDirectory(work);

try
{
    Run("record history persists and interrupted work is surfaced", TestHistory);
    Run("settings and protected key round-trip", TestSettings);
    Run("timestamped audio preserves silence and remains readable after a checkpoint", TestAlignedAudio);
    Run("an hour-long timeline remains valid when the output is silent for a long period", TestLongSilence);
    await TestTranscriptionResume();
    Console.WriteLine("PASS transcription resumes completed chunks without silently resending an uncertain request");
    await TestWholeTextFallback();
    Console.WriteLine("PASS full transcription is preserved when only the final sentence has a timestamp");
    await TestChunkBoundaries();
    Console.WriteLine("PASS overlapping audio chunks preserve source and avoid duplicate boundary sentences");
    Console.WriteLine("All smoke tests passed.");
}
finally
{
    if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
}

void Run(string name, Action test)
{
    test();
    Console.WriteLine($"PASS {name}");
}

void TestHistory()
{
    string databasePath = Path.Combine(work, "history.db");
    var repository = new RecordRepository(databasePath);
    var record = new InterviewRecord
    {
        Company = "测试公司", Position = "算法工程师", Round = "一面", FolderPath = @"D:\原始位置",
        MicrophonePath = @"D:\原始位置\microphone.wav", SystemAudioPath = @"D:\原始位置\system.wav",
        TranscriptPath = @"D:\原始位置\transcript.txt", Status = RecordStates.Recording
    };
    repository.Save(record);
    record.Company = "更新后的公司";
    repository.Save(record);
    var reopened = new RecordRepository(databasePath);
    var saved = reopened.List().Single();
    Equal("更新后的公司", saved.Company);
    Equal(@"D:\原始位置\microphone.wav", saved.MicrophonePath);
    reopened.MarkInterruptedSessions();
    Equal(RecordStates.Interrupted, reopened.List().Single().Status);
}

void TestSettings()
{
    var store = new AppSettingsStore(Path.Combine(work, "settings"));
    var settings = new AppSettings { DefaultSaveDirectory = @"D:\面试记录", DatabasePath = Path.Combine(work, "other.db") };
    store.Save(settings);
    Equal(settings.DefaultSaveDirectory, store.Load().DefaultSaveDirectory);
    store.SaveApiKey("synthetic-test-key");
    Equal("synthetic-test-key", store.ReadApiKey());
    byte[] stored = File.ReadAllBytes(Path.Combine(work, "settings", "api-key.bin"));
    if (System.Text.Encoding.UTF8.GetString(stored).Contains("synthetic-test-key", StringComparison.Ordinal))
        throw new Exception("API key was saved as plaintext.");
}

void TestAlignedAudio()
{
    string path = Path.Combine(work, "aligned.wav");
    var format = new WaveFormat(1000, 16, 1);
    const long origin = 10_000_000_000;
    using (var writer = new AlignedWaveWriter(path, format, origin))
    {
        writer.Append(Samples(100, 1000), origin);
        writer.Append(Samples(100, 2000), origin + 10_000_000);
        writer.Flush();
        using (var snapshotStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var snapshot = new WaveFileReader(snapshotStream)) Equal(1100L * 2, snapshot.Length);
        writer.Finish(origin + 20_000_000);
    }
    using var reader = new WaveFileReader(path);
    Equal(2000L * 2, reader.Length);
    byte[] bytes = new byte[(int)reader.Length];
    Equal(bytes.Length, reader.Read(bytes, 0, bytes.Length));
    Equal((short)1000, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(0, 2)));
    Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(500 * 2, 2)));
    Equal((short)2000, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(1000 * 2, 2)));
    Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(1500 * 2, 2)));
}

void TestLongSilence()
{
    string path = Path.Combine(work, "long-silence.wav");
    var format = new WaveFormat(16000, 16, 1);
    const long origin = 10_000_000_000;
    using (var writer = new AlignedWaveWriter(path, format, origin))
    {
        writer.Append(Samples(160, 1000), origin);
        writer.Append(Samples(160, 2000), origin + TimeSpan.FromSeconds(3599).Ticks);
        writer.Finish(origin + TimeSpan.FromHours(1).Ticks);
    }
    using var reader = new WaveFileReader(path);
    Equal(115_200_000L, reader.Length);
    reader.Position = 3599L * format.AverageBytesPerSecond;
    byte[] sample = new byte[2];
    Equal(2, reader.Read(sample, 0, 2));
    Equal((short)2000, BinaryPrimitives.ReadInt16LittleEndian(sample));
}

async Task TestTranscriptionResume()
{
    string folder = Path.Combine(work, "api-test");
    Directory.CreateDirectory(folder);
    var record = new InterviewRecord
    {
        FolderPath = folder,
        MicrophonePath = Path.Combine(folder, "microphone.wav"),
        SystemAudioPath = Path.Combine(folder, "system.wav"),
        TranscriptPath = Path.Combine(folder, "transcript.txt")
    };
    foreach (string path in new[] { record.MicrophonePath, record.SystemAudioPath })
    {
        using var wave = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        byte[] samples = Samples(16000, 123);
        wave.Write(samples, 0, samples.Length);
    }
    var handler = new FakeSpeechHandler();
    using var client = new HttpClient(handler);
    var service = new TranscriptionService(client);
    var settings = new AppSettings { ApiEndpoint = "https://example.invalid/speech", ApiModel = "test-model" };
    try
    {
        await service.RunAsync(record, settings, "test-key", false, null, CancellationToken.None);
        throw new Exception("Expected a simulated network failure.");
    }
    catch (HttpRequestException) { }
    Equal(2, handler.Calls);
    try
    {
        await service.RunAsync(record, settings, "test-key", false, null, CancellationToken.None);
        throw new Exception("An uncertain request must require explicit retry.");
    }
    catch (UncertainTranscriptionException) { }
    Equal(2, handler.Calls);
    string result = await service.RunAsync(record, settings, "test-key", true, null, CancellationToken.None);
    Equal(3, handler.Calls);
    if (!result.Contains("我（麦克风）：我的回答", StringComparison.Ordinal) ||
        !result.Contains("面试官（系统声音）：面试官问题", StringComparison.Ordinal))
        throw new Exception("The merged transcript did not preserve both sources.");
    Equal(result, File.ReadAllText(record.TranscriptPath));
    File.WriteAllText(record.TranscriptPath, "用户手动修改的文字");
    await service.RunAsync(record, settings, "test-key", false, null, CancellationToken.None);
    Equal(3, handler.Calls);
    string backup = Directory.GetFiles(folder, "transcript-backup-*.txt").Single();
    Equal("用户手动修改的文字", File.ReadAllText(backup));
}

async Task TestWholeTextFallback()
{
    string folder = Path.Combine(work, "whole-text-test");
    Directory.CreateDirectory(folder);
    var record = new InterviewRecord
    {
        FolderPath = folder,
        MicrophonePath = Path.Combine(folder, "microphone.wav"),
        SystemAudioPath = Path.Combine(folder, "system.wav"),
        TranscriptPath = Path.Combine(folder, "transcript.txt")
    };
    foreach (string path in new[] { record.MicrophonePath, record.SystemAudioPath })
    {
        using var wave = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        byte[] samples = Samples(16000, 123);
        wave.Write(samples, 0, samples.Length);
    }
    using var client = new HttpClient(new WholeTextHandler());
    var service = new TranscriptionService(client);
    string result = await service.RunAsync(record,
        new AppSettings { ApiEndpoint = "https://example.invalid/speech", ApiModel = "test-model" },
        "test-key", false, null, CancellationToken.None);
    if (!result.Contains("【片段时间，需回听核对】前半句。后半句。", StringComparison.Ordinal))
        throw new Exception("The entire response text must not be truncated to the final sentence.");
}

async Task TestChunkBoundaries()
{
    string folder = Path.Combine(work, "chunk-test");
    Directory.CreateDirectory(folder);
    var record = new InterviewRecord
    {
        FolderPath = folder, MicrophonePath = Path.Combine(folder, "microphone.wav"),
        SystemAudioPath = Path.Combine(folder, "system.wav"), TranscriptPath = Path.Combine(folder, "transcript.txt")
    };
    foreach (string path in new[] { record.MicrophonePath, record.SystemAudioPath })
    {
        using var wave = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        byte[] second = Samples(16000, 123);
        for (int i = 0; i < 121; i++) wave.Write(second, 0, second.Length);
    }
    var handler = new BoundarySpeechHandler();
    using var client = new HttpClient(handler);
    var service = new TranscriptionService(client);
    string result = await service.RunAsync(record,
        new AppSettings { ApiEndpoint = "https://example.invalid/speech", ApiModel = "test-model" },
        "test-key", false, null, CancellationToken.None);
    Equal(4, handler.Calls);
    Equal(2, result.Split("边界句", StringSplitOptions.None).Length - 1);
    File.SetLastWriteTimeUtc(record.MicrophonePath, File.GetLastWriteTimeUtc(record.MicrophonePath).AddSeconds(2));
    try
    {
        await service.RunAsync(record,
            new AppSettings { ApiEndpoint = "https://example.invalid/speech", ApiModel = "test-model" },
            "test-key", false, null, CancellationToken.None);
        throw new Exception("Changed input audio must not reuse previous transcription progress.");
    }
    catch (InvalidDataException) { }
    Equal(4, handler.Calls);
}

static byte[] Samples(int count, short value)
{
    byte[] bytes = new byte[count * 2];
    for (int i = 0; i < count; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), value);
    return bytes;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}.");
}

sealed class FakeSpeechHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (!body.Contains("data:audio/wav;base64,", StringComparison.Ordinal))
            throw new Exception("The request did not include an audio chunk.");
        if (Calls == 2) throw new HttpRequestException("Simulated connection interruption.");
        string text = Calls == 1 ? "我的回答" : "面试官问题";
        string payload = "data: {\"output\":{\"sentence\":{\"sentence_end\":true,\"sentence_id\":1,\"begin_time\":100,\"end_time\":500,\"text\":\"" + text + "\"}}}\n\n";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "text/event-stream")
        };
    }
}

sealed class WholeTextHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        const string payload = "{\n  \"output\": { \"text\": \"前半句。后半句。\", \"sentence\": { \"sentence_end\": true, \"begin_time\": 400, \"end_time\": 800, \"text\": \"后半句。\" } }\n}";
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

sealed class BoundarySpeechHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        string data = document.RootElement.GetProperty("input").GetProperty("messages")[0]
            .GetProperty("content")[0].GetProperty("input_audio").GetProperty("data").GetString()!;
        byte[] bytes = Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]);
        using var stream = new MemoryStream(bytes);
        using var wave = new WaveFileReader(stream);
        if (wave.TotalTime > TimeSpan.FromSeconds(124)) throw new Exception("Audio chunk exceeded the intended size.");
        long begin = Calls <= 2 ? 119900 : 1900;
        long end = begin + 400;
        string payload = $"data: {{\"output\":{{\"sentence\":{{\"sentence_end\":true,\"sentence_id\":1,\"begin_time\":{begin},\"end_time\":{end},\"text\":\"边界句\"}}}}}}\n\n";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "text/event-stream")
        };
    }
}
