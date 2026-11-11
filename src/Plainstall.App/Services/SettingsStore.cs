using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Plainstall.Core;
using Plainstall.Core.Profiles;

namespace Plainstall.App.Services;

/// <summary>
///     Keeps the app settings and the last profile under %LOCALAPPDATA%\Plainstall. Large data (the ISO cache,
///     the work folder, the build logs) is under ProgramData instead, see <see cref="Core.Pipeline.PlainstallPaths" />.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _folder;
    private readonly Lock _gate = new();
    private readonly ILogger<SettingsStore> _logger;
    private readonly ProfileSerializer _serializer;

    public SettingsStore(ProfileSerializer serializer, ILogger<SettingsStore> logger, string? folder = null)
    {
        _serializer = serializer;
        _logger = logger;
        _folder = folder ?? DefaultFolder;
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plainstall");

    private string SettingsPath => Path.Combine(_folder, "settings.json");
    private string LastProfilePath => Path.Combine(_folder, "last" + Profile.FileExtension);

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(SettingsPath)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ??
                      new AppSettings()
                    : new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Settings are a convenience. The next save replaces settings that cannot be read.
                _logger.LogWarning("The app settings could not be read: {Message}", ex.Message);
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("The app settings could not be saved: {Message}", ex.Message);
            }
        }
    }

    public Profile? LoadLastProfile()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(LastProfilePath) ? _serializer.Load(LastProfilePath).Profile : null;
            }
            catch (PlainstallException ex)
            {
                _logger.LogWarning("The last profile could not be read: {Message}", ex.Message);
                return null;
            }
        }
    }

    public void SaveLastProfile(Profile profile)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_folder);
            // The serializer never writes a password.
            _serializer.Save(profile, LastProfilePath);
        }
    }
}