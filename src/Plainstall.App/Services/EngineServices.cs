using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Plainstall.Core;
using Plainstall.Core.Media;
using Plainstall.Core.Pipeline;
using Plainstall.Core.Unattend;

namespace Plainstall.App.Services;

// The implementations that talk to the engine and the PC. The tests and the page renderer use only
// BootKeyData from this file.

public sealed class HostInfo : IHostInfo
{
    public HostInfo()
    {
        using var identity = WindowsIdentity.GetCurrent();
        IsElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var text = typeof(HostInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0];
        AppVersion = Version.TryParse(text?.Split('-')[0], out var version) ? version : new Version(0, 0, 0);
    }

    public bool IsElevated { get; }
    public CultureInfo DisplayCulture => CultureInfo.CurrentUICulture;
    public Version AppVersion { get; }
}

public sealed class EngineMediaReader(ILogger<EngineMediaReader> logger) : IMediaReader
{
    public async Task<MediaInfo> ReadAsync(string isoPath, CancellationToken cancellationToken)
    {
        await using var handle =
            await new MediaOpener().OpenAsync(isoPath, logger, cancellationToken).ConfigureAwait(false);
        return MediaInventory.Read(handle.RootPath, logger, cancellationToken);
    }
}

public sealed class EngineLeftoverService(ILogger<EngineLeftoverService> logger) : ILeftoverService
{
    public bool HasLeftovers(string workFolder)
    {
        try
        {
            return LeftoverCleaner.HasLeftovers(new WorkLayout(workFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public Task<CleanupReport> CleanAsync(string workFolder)
    {
        return Task.Run(() =>
            StoredData.CleanLeftovers(workFolder, StoredData.TryStartDism(PlainstallPaths.Default, logger), logger));
    }

    public long StoredDataBytes()
    {
        return StoredData.Size(PlainstallPaths.Default);
    }

    public Task<CleanupReport> DeleteStoredDataAsync()
    {
        var paths = PlainstallPaths.Default;
        return Task.Run(() => StoredData.Delete(paths, StoredData.TryStartDism(paths, logger), logger));
    }
}

public sealed class EnginePreflightService : IPreflightService
{
    public SpaceCheck Check(string workFolder, long requiredBytes)
    {
        var host = HostEnvironment.Detect(workFolder);
        // Elevation is shown as its own banner, so it is left out of the list here.
        var problems = Preflight.Check(workFolder, host with { IsElevated = true }, requiredBytes);
        return new SpaceCheck(requiredBytes, host.WorkDriveFormat is null ? null : host.WorkDriveFreeBytes, problems);
    }
}

public sealed class SystemLocaleCatalog : ILocaleCatalog
{
    private readonly Lazy<IReadOnlyList<LocaleOption>> _cultures = new(() =>
    [
        .. CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Where(c => SetupResolver.IsLanguageTag(c.Name))
            .Select(c => new LocaleOption(c.Name, c.DisplayName + " (" + c.Name + ")"))
            .OrderBy(o => o.DisplayName, StringComparer.CurrentCulture)
    ]);

    private readonly Lazy<IReadOnlyList<TimeZoneOption>> _zones = new(() =>
    [
        .. TimeZoneInfo.GetSystemTimeZones().Where(z => z.Id.Length <= 64)
            .Select(z => new TimeZoneOption(z.Id, z.DisplayName))
    ]);

    public IReadOnlyList<LocaleOption> Regions => _cultures.Value;
    public IReadOnlyList<LocaleOption> Keyboards => _cultures.Value;
    public IReadOnlyList<TimeZoneOption> TimeZones => _zones.Value;
}

/// <summary>Runs <see cref="BuildPipeline" /> on a worker thread with a log file per build, like the command-line tool.</summary>
public sealed class EngineBuildRunner : IBuildRunner
{
    public Task<BuildOutcome> RunAsync(BuildStart start, IProgress<BuildProgress> progress, IProgress<string> log,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            using var running = BuildLock.Take();
            var paths = PlainstallPaths.Default;
            var logPath = Path.Combine(paths.Logs, $"plainstall-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            using var fileLog = new FileLoggerProvider(logPath);
            using var relayLog = new RelayLoggerProvider(log);
            using var loggers = new LoggerFactory([fileLog, relayLog],
                new LoggerFilterOptions { MinLevel = LogLevel.Information });
            var services = DefaultServices.Create(paths, loggers, start.SourceIsoPath);
            var pipeline = new BuildPipeline(services, loggers.CreateLogger("Build"));
            var result = await pipeline.RunAsync(start.Request, progress, cancellationToken).ConfigureAwait(false);
            return new BuildOutcome(result.Report, result.ReportPath, result.IsoPath, logPath)
            {
                CleanupFinished = result.CleanupFinished
            };
        }, CancellationToken.None);
    }

    /// <summary>Passes each log line to the page's log view.</summary>
    private sealed class RelayLoggerProvider(IProgress<string> target) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return new Relay(target);
        }

        public void Dispose()
        {
        }

        private sealed class Relay(IProgress<string> target) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel >= LogLevel.Information;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    target.Report(string.Create(CultureInfo.InvariantCulture,
                        $"{DateTime.Now:HH:mm:ss}  {formatter(state, exception)}"));
            }
        }
    }
}

public static class BootKeyData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The boot-menu keys by PC brand, from the app's data file.</summary>
    public static IReadOnlyList<BootKey> Load()
    {
        using var stream = typeof(BootKeyData).Assembly.GetManifestResourceStream("Plainstall.App.Data.boot-keys.json")
                           ?? throw new InvalidOperationException("The boot key data is missing from the app.");
        return JsonSerializer.Deserialize<List<BootKey>>(stream, JsonOptions) ?? [];
    }
}