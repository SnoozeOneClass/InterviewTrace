using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InterviewTrace.Core;

public static class AppPaths
{
    public static string ConfigDirectory => Environment.GetEnvironmentVariable("INTERVIEWTRACE_CONFIG_DIR")
        is { Length: > 0 } value ? Path.GetFullPath(value)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterviewTrace");
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;
    private string SettingsPath => Path.Combine(_directory, "settings.json");
    private string SecretPath => Path.Combine(_directory, "api-key.bin");

    public AppSettingsStore(string? directory = null) => _directory = directory ?? AppPaths.ConfigDirectory;

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        using var file = File.OpenRead(SettingsPath);
        return JsonSerializer.Deserialize<AppSettings>(file, JsonOptions)
            ?? throw new InvalidDataException("设置文件为空或格式不正确。请检查设置文件后重试。");
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(_directory);
        AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public bool HasApiKey => File.Exists(SecretPath);

    public void SaveApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        Directory.CreateDirectory(_directory);
        byte[] plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
        try
        {
            byte[] encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            AtomicFile.WriteAllBytes(SecretPath, encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public string? ReadApiKey()
    {
        if (!File.Exists(SecretPath)) return null;
        byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(SecretPath), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void DeleteApiKey()
    {
        if (File.Exists(SecretPath)) File.Delete(SecretPath);
    }
}

public static class AtomicFile
{
    public static void WriteAllText(string path, string value) => WriteAllBytes(path, Encoding.UTF8.GetBytes(value));

    public static void WriteAllBytes(string path, byte[] value)
    {
        string fullPath = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("文件路径无效。", nameof(path));
        Directory.CreateDirectory(parent);
        string temporaryPath = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
