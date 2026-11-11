using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plainstall.Core.Catalog;

public enum RiskLevel
{
    Safe,
    Moderate,
    Advanced
}

public enum CatalogKind
{
    Apps,
    Tweaks
}

public sealed record CatalogCategory(string Id, string Title, string? Description = null);

public sealed record CatalogSource(string Name, string? Url = null);

public sealed record ReinstallInfo(string Method, string? Id = null, string? Note = null);

/// <summary>One switch in the catalog: an app that can be removed or a setting that can be changed.</summary>
public sealed record CatalogEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string? Details { get; init; }
    public required string Category { get; init; }
    public required RiskLevel Risk { get; init; }
    public required IReadOnlyList<string> DefaultIn { get; init; }
    public int? MinBuild { get; init; }
    public int? MaxBuild { get; init; }
    public IReadOnlyList<string>? Editions { get; init; }
    public IReadOnlyList<string> Requires { get; init; } = [];
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    public string? BoundTo { get; init; }
    public ReinstallInfo? Reinstall { get; init; }
    public IReadOnlyList<CatalogSource> Source { get; init; } = [];
    public required IReadOnlyList<CatalogAction> Actions { get; init; }

    /// <summary>Which file the entry came from. Set by the loader, not read from JSON.</summary>
    [JsonIgnore]
    public CatalogKind Kind { get; init; }
}

/// <summary>A file of entries: apps.json or tweaks.json.</summary>
public sealed record CatalogFile
{
    [JsonPropertyName("$schema")] public string? Schema { get; init; }

    public required int CatalogVersion { get; init; }
    public required CatalogKind Kind { get; init; }
    public required IReadOnlyList<CatalogCategory> Categories { get; init; }
    public IReadOnlyList<string> ProtectedPackages { get; init; } = [];
    public required IReadOnlyList<CatalogEntry> Entries { get; init; }
}

public sealed record PresetSetupDefaults(bool LocalAccount, bool SkipPrivacyPages, bool BypassHardwareChecks);

public sealed record Preset
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Tagline { get; init; }
    public required string Description { get; init; }
    public bool IsDefault { get; init; }
    public required PresetSetupDefaults Setup { get; init; }
}

public sealed record PresetsFile
{
    [JsonPropertyName("$schema")] public string? Schema { get; init; }

    public required int CatalogVersion { get; init; }
    public required IReadOnlyList<Preset> Presets { get; init; }
}

/// <summary>Registry hives an action can target. Each is a file inside the image.</summary>
public enum RegistryHiveId
{
    /// <summary><c>Windows\System32\config\SOFTWARE</c>: HKLM\SOFTWARE of the installed Windows.</summary>
    MachineSoftware,

    /// <summary><c>Windows\System32\config\SYSTEM</c>: HKLM\SYSTEM of the installed Windows.</summary>
    MachineSystem,

    /// <summary>
    ///     <c>Users\Default\NTUSER.DAT</c>: copied into every account created later, so it is HKCU for new users.
    ///     It does not contain <c>Software\Classes</c>. Windows creates that part of HKCU per account at first sign-in.
    /// </summary>
    DefaultUser,

    /// <summary><c>Windows\System32\config\DEFAULT</c>: HKU\.DEFAULT, the LocalSystem account and the sign-in screen.</summary>
    SystemUser,

    /// <summary>The SYSTEM hive of the Windows Setup image in <c>boot.wim</c>.</summary>
    SetupSystem
}

public enum RegistryValueType
{
    Dword,
    Qword,
    String,
    ExpandString,
    MultiString,
    Binary
}

public enum FirstLogonScope
{
    User,
    Machine
}

public enum FirstLogonRegistryOp
{
    Set,
    Delete
}

/// <summary>One concrete change. The <c>kind</c> property in JSON selects the subtype.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RegistrySetAction), "registry.set")]
[JsonDerivedType(typeof(RegistryDeleteAction), "registry.delete")]
[JsonDerivedType(typeof(AppxRemoveProvisionedAction), "appx.removeProvisioned")]
[JsonDerivedType(typeof(CapabilityRemoveAction), "capability.remove")]
[JsonDerivedType(typeof(FeatureDisableAction), "feature.disable")]
[JsonDerivedType(typeof(FeatureEnableAction), "feature.enable")]
[JsonDerivedType(typeof(FileDeleteAction), "file.delete")]
[JsonDerivedType(typeof(TaskDeleteAction), "task.delete")]
[JsonDerivedType(typeof(FirstLogonRegistryAction), "firstLogon.registry")]
[JsonDerivedType(typeof(FirstLogonCommandAction), "firstLogon.command")]
public abstract record CatalogAction
{
    public int? MinBuild { get; init; }
    public int? MaxBuild { get; init; }
    public string? Note { get; init; }

    /// <summary>The kind string as written in the catalog, for logs and the build report.</summary>
    [JsonIgnore]
    public abstract string KindName { get; }

    /// <summary>A short description of what the action touches, for logs and the build report.</summary>
    [JsonIgnore]
    public abstract string Target { get; }
}

public sealed record RegistrySetAction : CatalogAction
{
    public required RegistryHiveId Hive { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required RegistryValueType Type { get; init; }
    public required JsonElement Data { get; init; }
    public bool IfKeyExists { get; init; }

    public override string KindName => "registry.set";
    public override string Target => $@"{Hive}\{Key}\{(Name.Length == 0 ? "(Default)" : Name)}";
}

public sealed record RegistryDeleteAction : CatalogAction
{
    public required RegistryHiveId Hive { get; init; }
    public required string Key { get; init; }
    public string? Name { get; init; }

    public override string KindName => "registry.delete";

    public override string Target =>
        Name is null ? $@"{Hive}\{Key}" : $@"{Hive}\{Key}\{(Name.Length == 0 ? "(Default)" : Name)}";
}

public sealed record AppxRemoveProvisionedAction : CatalogAction
{
    public required string Package { get; init; }

    public override string KindName => "appx.removeProvisioned";
    public override string Target => Package;
}

public sealed record CapabilityRemoveAction : CatalogAction
{
    public required string Name { get; init; }

    public override string KindName => "capability.remove";
    public override string Target => Name;
}

public sealed record FeatureDisableAction : CatalogAction
{
    public required string Name { get; init; }

    public override string KindName => "feature.disable";
    public override string Target => Name;
}

public sealed record FeatureEnableAction : CatalogAction
{
    public required string Name { get; init; }

    public override string KindName => "feature.enable";
    public override string Target => Name;
}

public sealed record FileDeleteAction : CatalogAction
{
    public required string Path { get; init; }
    public bool Recurse { get; init; }

    public override string KindName => "file.delete";
    public override string Target => Path;
}

public sealed record TaskDeleteAction : CatalogAction
{
    public required string Path { get; init; }
    public bool Folder { get; init; }

    public override string KindName => "task.delete";
    public override string Target => Path;
}

public sealed record FirstLogonRegistryAction : CatalogAction
{
    public required FirstLogonRegistryOp Op { get; init; }
    public required string Key { get; init; }
    public string? Name { get; init; }
    public RegistryValueType? Type { get; init; }
    public JsonElement? Data { get; init; }

    public override string KindName => "firstLogon.registry";

    public override string Target =>
        $"HKCU\\{Key}" + (Name is null ? "" : $"\\{(Name.Length == 0 ? "(Default)" : Name)}");
}

public sealed record FirstLogonCommandAction : CatalogAction
{
    public required FirstLogonScope Scope { get; init; }
    public required string Command { get; init; }
    public required string Summary { get; init; }

    public override string KindName => "firstLogon.command";
    public override string Target => Summary;
}