using System.Globalization;
using Microsoft.Data.Sqlite;

namespace InterviewTrace.Core;

public sealed class RecordRepository
{
    public string DatabasePath { get; }
    private readonly string _connectionString;

    public RecordRepository(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS records (
              id TEXT PRIMARY KEY, created_at TEXT NOT NULL,
              company TEXT NOT NULL, position TEXT NOT NULL, round TEXT NOT NULL,
              interview_date TEXT NOT NULL, folder_path TEXT NOT NULL,
              microphone_path TEXT NOT NULL, system_audio_path TEXT NOT NULL,
              transcript_path TEXT NOT NULL, status TEXT NOT NULL,
              duration_ms INTEGER NOT NULL, last_error TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public void Save(InterviewRecord record)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO records (id, created_at, company, position, round, interview_date,
              folder_path, microphone_path, system_audio_path, transcript_path, status, duration_ms, last_error)
            VALUES ($id, $created, $company, $position, $round, $date,
              $folder, $mic, $system, $transcript, $status, $duration, $error)
            ON CONFLICT(id) DO UPDATE SET
              company=excluded.company, position=excluded.position, round=excluded.round,
              interview_date=excluded.interview_date, folder_path=excluded.folder_path,
              microphone_path=excluded.microphone_path, system_audio_path=excluded.system_audio_path,
              transcript_path=excluded.transcript_path, status=excluded.status,
              duration_ms=excluded.duration_ms, last_error=excluded.last_error;
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$created", record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$company", record.Company ?? "");
        command.Parameters.AddWithValue("$position", record.Position ?? "");
        command.Parameters.AddWithValue("$round", record.Round ?? "");
        command.Parameters.AddWithValue("$date", record.InterviewDate ?? "");
        command.Parameters.AddWithValue("$folder", record.FolderPath ?? "");
        command.Parameters.AddWithValue("$mic", record.MicrophonePath ?? "");
        command.Parameters.AddWithValue("$system", record.SystemAudioPath ?? "");
        command.Parameters.AddWithValue("$transcript", record.TranscriptPath ?? "");
        command.Parameters.AddWithValue("$status", record.Status ?? "");
        command.Parameters.AddWithValue("$duration", record.DurationMilliseconds);
        command.Parameters.AddWithValue("$error", record.LastError ?? "");
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<InterviewRecord> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM records ORDER BY created_at DESC";
        using var reader = command.ExecuteReader();
        var records = new List<InterviewRecord>();
        while (reader.Read())
        {
            records.Add(new InterviewRecord
            {
                Id = reader.GetString(0),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Company = reader.GetString(2), Position = reader.GetString(3), Round = reader.GetString(4),
                InterviewDate = reader.GetString(5), FolderPath = reader.GetString(6),
                MicrophonePath = reader.GetString(7), SystemAudioPath = reader.GetString(8),
                TranscriptPath = reader.GetString(9), Status = reader.GetString(10),
                DurationMilliseconds = reader.GetInt64(11), LastError = reader.GetString(12)
            });
        }
        return records;
    }

    public void MarkInterruptedSessions()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE records SET status=$interrupted, last_error=$recording_error WHERE status=$recording;
            UPDATE records SET status=$transcription_interrupted, last_error=$transcription_error WHERE status=$transcribing;
            """;
        command.Parameters.AddWithValue("$interrupted", RecordStates.Interrupted);
        command.Parameters.AddWithValue("$recording_error", "上次录音未正常结束，请先回听已保存的音频。");
        command.Parameters.AddWithValue("$recording", RecordStates.Recording);
        command.Parameters.AddWithValue("$transcription_interrupted", RecordStates.TranscriptionInterrupted);
        command.Parameters.AddWithValue("$transcription_error", "上次转写未正常结束，请检查已完成的片段后再继续。");
        command.Parameters.AddWithValue("$transcribing", RecordStates.Transcribing);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
