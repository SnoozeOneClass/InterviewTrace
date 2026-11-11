using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace InterviewTrace.Core;

public sealed class UncertainTranscriptionException(string message) : Exception(message);

/// <summary>Submits local WAV chunks only after an explicit user action and checkpoints each paid request.</summary>
public sealed class TranscriptionService(HttpClient httpClient)
{
    private const int ChunkMilliseconds = 120_000;
    private const int OverlapMilliseconds = 2_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public async Task<string> RunAsync(InterviewRecord record, AppSettings settings, string apiKey,
        bool retryUncertain, IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!Uri.TryCreate(settings.ApiEndpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("转写接口地址必须是 HTTPS 地址。");
        if (!File.Exists(record.MicrophonePath) || !File.Exists(record.SystemAudioPath))
            throw new FileNotFoundException("找不到两路录音文件，请先重新定位记录文件夹。");

        var sources = new[]
        {
            new Source("mic", "我（麦克风）", record.MicrophonePath),
            new Source("system", "面试官（系统声音）", record.SystemAudioPath)
        };
        List<Job> jobs = BuildJobs(sources);
        if (jobs.Count == 0) throw new InvalidDataException("录音文件没有可转写的音频数据。");
        string checkpointPath = Path.Combine(record.FolderPath, ".transcription-checkpoint.json");
        string fingerprint = string.Join("|", sources.Select(s => Fingerprint(s.Path))) + $"|{ChunkMilliseconds}|{OverlapMilliseconds}|{settings.ApiModel}";
        Checkpoint checkpoint = LoadCheckpoint(checkpointPath, fingerprint);
        if (!retryUncertain && checkpoint.Chunks.Values.Any(c => c.State == "submitting"))
            throw new UncertainTranscriptionException("上一次提交未收到确定结果；重试该片段可能重复计费。");

        int completed = jobs.Count(j => checkpoint.Chunks.TryGetValue(j.Key, out var saved) && saved.State == "completed");
        foreach (Job job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checkpoint.Chunks.TryGetValue(job.Key, out ChunkResult? saved) && saved.State == "completed") continue;
            progress?.Report(new TranscriptionProgress(completed, jobs.Count, $"正在处理{job.Source.Label}，第 {completed + 1}/{jobs.Count} 段"));
            byte[] wave = ReadChunk(job); // local errors cannot have caused a billable request
            checkpoint.Chunks[job.Key] = new ChunkResult { State = "submitting" };
            SaveCheckpoint(checkpointPath, checkpoint); // persist before the potentially billable request
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromMinutes(6));
                List<TranscriptSegment> segments;
                try
                {
                    segments = await SendAsync(endpoint, settings.ApiModel, apiKey, wave, job,
                        requestTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("云端转写超时，提交状态可能不明；再次尝试前请确认可能的重复计费。");
                }
                checkpoint.Chunks[job.Key] = new ChunkResult { State = "completed", Segments = segments };
                SaveCheckpoint(checkpointPath, checkpoint);
            }
            catch
            {
                // A request may have reached the service even if its response was lost.
                checkpoint.Chunks[job.Key] = new ChunkResult { State = "submitting" };
                try { SaveCheckpoint(checkpointPath, checkpoint); } catch { }
                throw;
            }
            completed++;
            progress?.Report(new TranscriptionProgress(completed, jobs.Count, $"已完成 {completed}/{jobs.Count} 段"));
        }

        var allSegments = checkpoint.Chunks.Values.Where(c => c.State == "completed")
            .SelectMany(c => c.Segments).OrderBy(s => s.BeginMilliseconds)
            .ThenBy(s => s.Source, StringComparer.Ordinal).ToArray();
        var text = new StringBuilder();
        foreach (TranscriptSegment segment in allSegments)
        {
            var time = TimeSpan.FromMilliseconds(Math.Max(0, segment.BeginMilliseconds));
            text.Append('[').Append((int)time.TotalHours).Append(':')
                .Append(time.Minutes.ToString("00", CultureInfo.InvariantCulture)).Append(':')
                .Append(time.Seconds.ToString("00", CultureInfo.InvariantCulture)).Append("] ")
                .Append(segment.Source).Append('：').AppendLine(segment.Text.Trim());
        }
        if (allSegments.Length == 0) text.AppendLine("未识别到语音，请回听原始录音确认。");
        string result = text.ToString();
        if (File.Exists(record.TranscriptPath) && File.ReadAllText(record.TranscriptPath) != result)
        {
            string backup = Path.Combine(record.FolderPath, $"transcript-backup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            File.Copy(record.TranscriptPath, backup, overwrite: false);
        }
        AtomicFile.WriteAllText(record.TranscriptPath, result);
        return result;
    }

    private async Task<List<TranscriptSegment>> SendAsync(Uri endpoint, string model, string apiKey,
        byte[] wave, Job job, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model,
            input = new { messages = new[] { new { role = "user", content = new[]
                { new { type = "input_audio", input_audio = new { data = "data:audio/wav;base64," + Convert.ToBase64String(wave) } } } } } },
            parameters = new { format = "wav", sample_rate = "16000" }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("X-DashScope-SSE", "enable");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"云端转写请求失败（HTTP {(int)response.StatusCode}），请检查密钥、模型权限和网络。", null, response.StatusCode);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var segments = new Dictionary<string, TranscriptSegment>();
        string? accumulatedText = null;
        bool sawOutput = false;
        int anonymousId = 0;
        var eventData = new StringBuilder();
        bool isEventStream = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        if (!isEventStream)
        {
            ProcessEvent(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        }
        else
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    ProcessEvent(eventData.ToString());
                    eventData.Clear();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (eventData.Length != 0) eventData.Append('\n');
                    eventData.Append(line.AsSpan(5).TrimStart());
                }
            }
            ProcessEvent(eventData.ToString());
        }
        if (!sawOutput) throw new InvalidDataException("云端没有返回可解析的转写结果。");
        if (!string.IsNullOrWhiteSpace(accumulatedText))
        {
            string joined = string.Concat(segments.Values.OrderBy(s => s.BeginMilliseconds).Select(s => s.Text));
            if (Normalize(joined) != Normalize(accumulatedText))
            {
                // Some non-streaming/short responses contain the entire text but only the final sentence timestamp.
                // Preserve the entire text and explicitly label the time as approximate instead of dropping speech.
                segments.Clear();
                segments["fallback"] = new TranscriptSegment(job.BaseStart, job.BaseEnd, job.Source.Label,
                    "【片段时间，需回听核对】" + accumulatedText);
            }
        }
        return segments.Values.Where(s =>
        {
            long midpoint = s.BeginMilliseconds + (s.EndMilliseconds - s.BeginMilliseconds) / 2;
            return midpoint >= job.BaseStart && (midpoint < job.BaseEnd || (job.IsLast && midpoint <= job.BaseEnd));
        }).ToList();

        void ProcessEvent(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "[DONE]") return;
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.String)
                throw new InvalidDataException($"云端转写返回错误：{code.GetString()}。");
            if (!root.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Object) return;
            sawOutput = true;
            if (output.TryGetProperty("text", out JsonElement fullText) && fullText.ValueKind == JsonValueKind.String)
                accumulatedText = fullText.GetString();
            if (!output.TryGetProperty("sentence", out JsonElement sentence) || sentence.ValueKind != JsonValueKind.Object) return;
            if (sentence.TryGetProperty("sentence_end", out JsonElement isFinal) && isFinal.ValueKind == JsonValueKind.False) return;
            if (!sentence.TryGetProperty("text", out JsonElement part) || part.ValueKind != JsonValueKind.String) return;
            string? value = part.GetString();
            if (string.IsNullOrWhiteSpace(value)) return;
            long begin = ReadNumber(sentence, "begin_time", 0);
            long end = Math.Max(begin, ReadNumber(sentence, "end_time", begin));
            string id = sentence.TryGetProperty("sentence_id", out JsonElement number) ? number.ToString() : $"anonymous-{anonymousId++}";
            segments[id] = new TranscriptSegment(job.WindowStart + begin, job.WindowStart + end, job.Source.Label, value);
        }
    }

    private static long ReadNumber(JsonElement element, string name, long fallback) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : fallback;

    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static List<Job> BuildJobs(IEnumerable<Source> sources)
    {
        var jobs = new List<Job>();
        foreach (Source source in sources)
        {
            using var reader = new WaveFileReader(source.Path);
            if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1)
                throw new InvalidDataException("录音格式不是预期的 16kHz 单声道 WAV，请勿修改原始录音后直接转写。");
            long duration = (long)Math.Ceiling(reader.TotalTime.TotalMilliseconds);
            for (long start = 0; start < duration; start += ChunkMilliseconds)
            {
                long end = Math.Min(duration, start + ChunkMilliseconds);
                jobs.Add(new Job(source, start, end, Math.Max(0, start - OverlapMilliseconds),
                    Math.Min(duration, end + OverlapMilliseconds), end == duration));
            }
        }
        return jobs.OrderBy(j => j.BaseStart).ThenBy(j => j.Source.Id, StringComparer.Ordinal).ToList();
    }

    private static byte[] ReadChunk(Job job)
    {
        using var reader = new WaveFileReader(job.Source.Path);
        int block = reader.WaveFormat.BlockAlign;
        long startByte = job.WindowStart * reader.WaveFormat.AverageBytesPerSecond / 1000;
        startByte -= startByte % block;
        long endByte = Math.Min(reader.Length, job.WindowEnd * reader.WaveFormat.AverageBytesPerSecond / 1000);
        endByte -= endByte % block;
        reader.Position = startByte;
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, reader.WaveFormat))
        {
            byte[] buffer = new byte[64 * 1024];
            long remaining = endByte - startByte;
            while (remaining > 0)
            {
                int count = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count <= 0) break;
                writer.Write(buffer, 0, count);
                remaining -= count;
            }
        }
        return output.ToArray();
    }

    private static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private static Checkpoint LoadCheckpoint(string path, string fingerprint)
    {
        if (!File.Exists(path)) return new Checkpoint { Fingerprint = fingerprint };
        Checkpoint? checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path), JsonOptions);
        if (checkpoint is null || checkpoint.Fingerprint != fingerprint)
            throw new InvalidDataException("录音或模型设置与上次转写不同，不能安全复用之前的转写进度。请保留原文件并检查设置。");
        return checkpoint;
    }

    private static void SaveCheckpoint(string path, Checkpoint checkpoint) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(checkpoint, JsonOptions));

    private sealed record Source(string Id, string Label, string Path);
    private sealed record Job(Source Source, long BaseStart, long BaseEnd, long WindowStart, long WindowEnd, bool IsLast)
    {
        public string Key => $"{Source.Id}:{BaseStart}";
    }
    private sealed class Checkpoint
    {
        public string Fingerprint { get; set; } = "";
        public Dictionary<string, ChunkResult> Chunks { get; set; } = new(StringComparer.Ordinal);
    }
    private sealed class ChunkResult
    {
        public string State { get; set; } = "";
        public List<TranscriptSegment> Segments { get; set; } = [];
    }
}
