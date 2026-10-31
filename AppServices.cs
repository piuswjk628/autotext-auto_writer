using System.Net;
using System.Text.Json;

namespace AutoInput;

static class AppPaths
{
    internal static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoInput");
    internal static string LogDirectory => Path.Combine(Root, "logs");
    internal static void WriteJson<T>(string name, T value)
    {
        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, true);
    }
}

static class ErrorLog
{
    static readonly object Sync = new();
    internal static void Write(Exception exception, string operation)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                File.AppendAllText(Path.Combine(AppPaths.LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    $"[{DateTimeOffset.Now:O}] {operation}\n{exception}\n\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Logging must not crash the app. */ }
    }
}

sealed record SavedWork(string Text, int Offset, string DocumentName)
{
    internal static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    internal SavedWork Validate()
    {
        int offset = Math.Clamp(Offset, 0, Text.Length);
        if (offset > 0 && offset < Text.Length && char.IsLowSurrogate(Text[offset]) && char.IsHighSurrogate(Text[offset - 1])) offset--;
        return this with { Offset = offset };
    }
    internal void Save() => AppPaths.WriteJson("work.json", this);
    internal static SavedWork? Load()
    {
        string path = Path.Combine(AppPaths.Root, "work.json");
        if (!File.Exists(path)) return null;
        var value = JsonSerializer.Deserialize<SavedWork>(File.ReadAllText(path));
        if (value?.Text == null) throw new InvalidDataException("저장된 작업 내용이 올바르지 않습니다.");
        return value.Validate();
    }
    internal static void Clear()
    {
        string path = Path.Combine(AppPaths.Root, "work.json");
        if (File.Exists(path)) File.Delete(path);
    }
}

sealed record ReleaseInfo(Version Version, string Tag);
static class UpdateService
{
    internal static readonly Version CurrentVersion = new(1, 1, 0);
    internal static ReleaseInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            throw new InvalidDataException("배포 버전 형식이 올바르지 않습니다. v1.1.0 같은 태그를 사용하세요.");
        return new ReleaseInfo(version, tag);
    }
    internal static async Task<ReleaseInfo?> Fetch(CancellationToken token, HttpMessageHandler? handler = null)
    {
        using var client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoInput/1.1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.GetAsync("https://api.github.com/repos/piuswjk628/autotext-auto_writer/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            throw new HttpRequestException("GitHub 요청 한도에 도달했습니다. 잠시 후 다시 확인하세요.");
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(token));
    }
}
