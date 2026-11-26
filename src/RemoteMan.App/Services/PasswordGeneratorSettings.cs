using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteMan.Core.Security;

namespace RemoteMan.App.Services;

/// <summary>
/// The password generator's last settings and your own templates, kept on this PC in
/// %AppData%\RemoteMan\password-generator.json (no passwords are ever stored here).
/// </summary>
public sealed class PasswordGeneratorSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static string FilePath => Path.Combine(AppPaths.Root, "password-generator.json");

    public PasswordOptions Last { get; set; } = PasswordGenerator.BuiltIn[0].Clone();
    public string? LastTemplate { get; set; }
    public int Count { get; set; } = 10;
    public List<PasswordOptions> Templates { get; set; } = new();

    public static PasswordGeneratorSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<PasswordGeneratorSettings>(File.ReadAllText(FilePath), Json) is { } s) return s;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
