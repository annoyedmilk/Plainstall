using System.Text.Json.Serialization;

namespace Plainstall.Core.Profiles;

public enum WindowsSource
{
    /// <summary>Get the current Windows 11 ISO from Microsoft.</summary>
    Download,

    /// <summary>The user supplies an ISO at build time.</summary>
    Iso
}

/// <summary>
///     Everything a user chose for a build. Holds choices, not machine state: no ISO path, no USB disk,
///     no output path and never a password. The file format is described by profile.schema.json in this folder.
/// </summary>
public sealed record Profile
{
    public const int CurrentSchema = 1;
    public const string FileExtension = ".plainstall.json";

    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SchemaUri { get; init; }

    [JsonPropertyOrder(-1)] public required int Schema { get; init; }

    public required string Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedWith { get; init; }

    public required WindowsOptions Windows { get; init; }
    public required AppSelection Apps { get; init; }
    public required TweakSelection Tweaks { get; init; }
    public required SetupOptions Setup { get; init; }
    public DriverOptions Drivers { get; init; } = new();
    public IReadOnlyList<string> Updates { get; init; } = [];
    public CleanupOptions Cleanup { get; init; } = new();
    public VerifyOptions Verify { get; init; } = new();
    public OutputOptions Output { get; init; } = new();
}

public sealed record WindowsOptions
{
    public required WindowsSource Source { get; init; }

    /// <summary>"auto" or a language tag such as de-DE.</summary>
    public required string Language { get; init; }

    /// <summary>EditionId: Professional, Core, Education and so on.</summary>
    public required string Edition { get; init; }
}

public sealed record AppSelection
{
    public required string Preset { get; init; }
    public IReadOnlyList<string> Keep { get; init; } = [];
    public IReadOnlyList<string> Remove { get; init; } = [];

    /// <summary>winget package ids or Microsoft Store ids to install at the first sign-in.</summary>
    public IReadOnlyList<string> Install { get; init; } = [];
}

public sealed record TweakSelection
{
    public required string Preset { get; init; }
    public IReadOnlyList<string> Enable { get; init; } = [];
    public IReadOnlyList<string> Disable { get; init; } = [];
}

public sealed record LocalAccountOptions
{
    /// <summary>null: Setup asks for the name. The Microsoft account pages are skipped either way.</summary>
    public required string? Name { get; init; }

    public bool AskPassword { get; init; }

    /// <summary>Always null. Present so that a profile trying to carry a password is rejected with a clear message.</summary>
    public string? Password { get; init; }
}

public sealed record SetupOptions
{
    /// <summary>null: Setup runs its normal (Microsoft account) pages.</summary>
    public required LocalAccountOptions? LocalAccount { get; init; }

    public string? ComputerName { get; init; }
    public required bool SkipPrivacyPages { get; init; }

    /// <summary>"auto", "ask" or a tag such as de-CH.</summary>
    public string Region { get; init; } = "auto";

    /// <summary>"auto", "ask" or a tag such as de-CH.</summary>
    public string Keyboard { get; init; } = "auto";

    /// <summary>"auto", "ask" (Windows picks one from the region) or a Windows time zone id.</summary>
    public string Timezone { get; init; } = "auto";

    public required bool BypassHardwareChecks { get; init; }
}

public sealed record DriverOptions
{
    public bool FromThisPc { get; init; }
    public IReadOnlyList<string> Folders { get; init; } = [];
}

public sealed record CleanupOptions
{
    public bool ResetBase { get; init; }
}

/// <summary>Checks that run on the finished image, before it is saved.</summary>
public sealed record VerifyOptions
{
    /// <summary>DISM ScanHealth on the component store. Damage stops the build. Adds several minutes.</summary>
    public bool ScanHealth { get; init; }
}

public sealed record OutputOptions
{
    public bool Usb { get; init; } = true;
    public bool Iso { get; init; }
}