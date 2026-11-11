namespace InterviewTrace.Core;

public static class RecordStates
{
    public const string Recording = "录音中";
    public const string Recorded = "待转写";
    public const string Interrupted = "录音中断";
    public const string Transcribing = "转写中";
    public const string TranscriptionInterrupted = "转写未完成";
    public const string Transcribed = "已转写";
}

public sealed class InterviewRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Company { get; set; } = "";
    public string Position { get; set; } = "";
    public string Round { get; set; } = "";
    public string InterviewDate { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string MicrophonePath { get; set; } = "";
    public string SystemAudioPath { get; set; } = "";
    public string TranscriptPath { get; set; } = "";
    public string Status { get; set; } = RecordStates.Recording;
    public long DurationMilliseconds { get; set; }
    public string LastError { get; set; } = "";
}

public sealed class AppSettings
{
    public string DefaultSaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "InterviewTrace");
    public string DatabasePath { get; set; } = Path.Combine(AppPaths.ConfigDirectory, "history.db");
    public string MicrophoneDeviceId { get; set; } = "";
    public string PlaybackDeviceId { get; set; } = "";
    public string ApiEndpoint { get; set; } = "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";
    public string ApiModel { get; set; } = "fun-asr-flash-2026-06-15";
}

public sealed record AudioDevice(string Id, string Name, bool IsDefault, bool IsCommunicationsDefault)
{
    public override string ToString()
    {
        string flags = IsDefault && IsCommunicationsDefault ? "（默认 / 通信）"
            : IsDefault ? "（默认）" : IsCommunicationsDefault ? "（默认通信）" : "";
        return Name + flags;
    }
}

public sealed record TranscriptSegment(long BeginMilliseconds, long EndMilliseconds, string Source, string Text);

public sealed record TranscriptionProgress(int Completed, int Total, string Message);
