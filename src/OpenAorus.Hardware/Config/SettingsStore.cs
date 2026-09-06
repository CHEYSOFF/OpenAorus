using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAorus.Hardware.Config;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        IncludeFields = false,
    };

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAorus", "settings.json");

    public string Path { get; }

    /// <summary>True after a call to <see cref="Load"/> that could not read the existing settings file and fell
    /// back to defaults (corrupt JSON, or the file could not be read at all). The caller is expected to tell the
    /// owner, since a silent reset can strand a takeover with no route back.</summary>
    public bool LastLoadWasReset { get; private set; }

    public SettingsStore(string path) => Path = path;

    public AppSettings Load()
    {
        LastLoadWasReset = false;
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Broad on purpose: a corrupt-JSON read throws JsonException, but a locked/unreadable file throws
            // IOException or UnauthorizedAccessException instead, and either way the owner needs to be told
            // rather than have the app fall over or silently strand a GCC takeover with no route back.
            LastLoadWasReset = true;
            try
            {
                var bad = Path + ".bad";
                if (File.Exists(bad)) File.Delete(bad);
                File.Move(Path, bad);
            }
            catch (Exception)
            {
                // The rename itself can fail (e.g. the file is still locked) - defaults are still returned below.
            }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, Path, overwrite: true);
    }
}
