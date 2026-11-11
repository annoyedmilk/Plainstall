using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Plainstall.Core.Profiles;

namespace Plainstall.Core.Pipeline;

/// <summary>Where a build is. Reported to the CLI and the app.</summary>
/// <param name="Percent">0 to 100 for the current step, or null when the step cannot tell.</param>
public sealed record BuildProgress(
    int StageNumber,
    int StageCount,
    string StageName,
    string? Action = null,
    int? Percent = null);

public sealed record ReportedAction(string Entry, string Kind, string Target, string Result, string? Reason = null);

public sealed record ReportSource
{
    public required string Kind { get; init; }
    public required string File { get; init; }
    public string? Sha256 { get; init; }
    public bool VerifiedAgainstMicrosoft { get; init; }
    public required string Edition { get; init; }
    public required string Build { get; init; }
    public required string Arch { get; init; }
    public string? Language { get; init; }
}

public sealed record ReportIso(string Path, long SizeBytes, string Sha256);

/// <summary>The USB stick that was written.</summary>
/// <param name="Ejected">True when Windows confirmed that the stick can be unplugged.</param>
public sealed record ReportUsb(
    string Disk,
    string? SerialNumber,
    long DiskSizeBytes,
    string VolumeRoot,
    long BytesWritten,
    bool Ejected);

public sealed record ReportOutput
{
    public ReportIso? Iso { get; init; }
    public ReportUsb? Usb { get; init; }
    public string? StagingFolder { get; init; }
    public long InstallImageBytes { get; init; }
    public bool Split { get; init; }
}

/// <summary>
///     The record of one build: what went in, what was done action by action, what came out. Written as
///     <c>build-report.json</c>. The VM test harness reads it to know what to verify in the installed system.
/// </summary>
public sealed record BuildReport
{
    /// <summary>The name of the report file.</summary>
    public const string FileName = "build-report.json";

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    public required string Plainstall { get; init; }
    public required int CatalogVersion { get; init; }
    public required DateTimeOffset Started { get; init; }
    public DateTimeOffset? Finished { get; init; }

    /// <summary>succeeded, completed-with-failures, failed or cancelled.</summary>
    public required string Result { get; init; }

    public string? Error { get; init; }
    public ReportSource? Source { get; init; }

    /// <summary>The profile as it would be saved. It never contains a password.</summary>
    public required Profile Profile { get; init; }

    public IReadOnlyList<ReportedAction> Actions { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> WingetInstalls { get; init; } = [];
    public ReportOutput? Output { get; init; }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Nulls are written: the embedded profile has required properties whose value may be null.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine;
    }

    public static BuildReport FromJson(string json)
    {
        return JsonSerializer.Deserialize<BuildReport>(json, JsonOptions)
               ?? throw new PlainstallException(ErrorKind.InvalidInput, "The build report is empty.");
    }

    public void Save(string path)
    {
        AtomicFile.WriteAllText(path, ToJson());
    }
}

/// <summary>Collects action outcomes, notes and warnings while a build runs. Thread-safe.</summary>
public sealed class BuildLog
{
    private readonly List<ReportedAction> _actions = [];
    private readonly Lock _gate = new();
    private readonly List<string> _warnings = [];

    public IReadOnlyList<ReportedAction> Actions
    {
        get
        {
            lock (_gate)
            {
                return [.. _actions];
            }
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate)
            {
                return [.. _warnings];
            }
        }
    }

    public int FailedCount
    {
        get
        {
            lock (_gate)
            {
                return _actions.Count(a => a.Result == "failed");
            }
        }
    }

    /// <summary>Records one action: a catalog change, or an update or a driver that the user added.</summary>
    public void Record(string entry, string kind, string target, ActionOutcome outcome)
    {
        var result = outcome.Status switch
        {
            ActionStatus.Applied => "applied",
            ActionStatus.Skipped => "skipped",
            _ => "failed"
        };
        lock (_gate)
        {
            _actions.Add(new ReportedAction(entry, kind, target, result, outcome.Reason));
        }
    }

    public void Warn(string message)
    {
        lock (_gate)
        {
            _warnings.Add(message);
        }
    }
}