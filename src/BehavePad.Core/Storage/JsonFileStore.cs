using System.Text.Json;
using System.Text.Json.Serialization;

namespace BehavePad.Core.Storage;

public static class BehavePadJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Saves one object as JSON. Writes go to a temp file first so a crash never leaves half a file.</summary>
public sealed class JsonFileStore<T>
    where T : class
{
    public JsonFileStore(string path)
    {
        FilePath = path;
    }

    public string FilePath { get; }

    public T? Load()
    {
        string json;
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, BehavePadJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Keep the unreadable file for troubleshooting and start fresh.
            try
            {
                File.Move(FilePath, FilePath + ".corrupt", overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }
    }

    public void Save(T value)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, BehavePadJson.Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}

public static class AppPaths
{
    /// <summary>Where BehavePad keeps its files. Set BEHAVEPAD_DATA_DIR to use a different folder, for example in tests.</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("BEHAVEPAD_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BehavePad");

    public static string ProfilePath => Path.Combine(DataDirectory, "profile.json");

    public static string ReportPath => Path.Combine(DataDirectory, "last-test.json");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string HiddenDevicesPath => Path.Combine(DataDirectory, "hidden-devices.json");
}
