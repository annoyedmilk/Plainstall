using System.Globalization;
using Plainstall.App.Resources;
using Plainstall.App.Services;
using Plainstall.Core;
using Plainstall.Core.Profiles;

namespace Plainstall.App.ViewModels;

/// <summary>The result of opening or saving a profile file. <see cref="Error" /> is a sentence for the user.</summary>
public sealed record ProfileFileResult(Profile? Profile, string? Path, IReadOnlyList<string> Warnings, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Open and save profile files through the dialogs, remembering the folder.</summary>
public sealed class ProfileFiles(
    IDialogService dialogs,
    ISettingsStore settings,
    IHostInfo host,
    ProfileSerializer serializer)
{
    public string CreatedWith => "Plainstall " + host.AppVersion.ToString(3);

    /// <summary>Null when the user cancelled the dialog.</summary>
    public ProfileFileResult? Open()
    {
        var current = settings.Load();
        var path = dialogs.PickProfileToOpen(current.LastProfileFolder);
        if (path is null) return null;
        Remember(path);
        try
        {
            var loaded = serializer.Load(path);
            return new ProfileFileResult(loaded.Profile, path, loaded.Warnings, null);
        }
        catch (PlainstallException ex)
        {
            return new ProfileFileResult(null, path, [], ex.Message);
        }
    }

    /// <summary>Null when the user cancelled the dialog. The saved profile is named after the file.</summary>
    public ProfileFileResult? Save(Profile profile)
    {
        var current = settings.Load();
        var path = dialogs.PickProfileSaveLocation(SuggestedFileName(profile), current.LastProfileFolder);
        if (path is null) return null;
        Remember(path);
        var name = NameFromPath(path) ?? profile.Name;
        var toSave = profile with { Name = name, CreatedWith = CreatedWith };
        try
        {
            serializer.Save(toSave, path);
            return new ProfileFileResult(toSave, path, [], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlainstallException)
        {
            return new ProfileFileResult(null, path, [],
                string.Format(CultureInfo.CurrentCulture, Strings.Profile_SaveFailed, ex.Message));
        }
    }

    public static string SuggestedFileName(Profile profile)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(profile.Name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return (safe.Length == 0 ? "profile" : safe) + Profile.FileExtension;
    }

    internal static string? NameFromPath(string path)
    {
        var file = Path.GetFileName(path);
        var name = file.EndsWith(Profile.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? file[..^Profile.FileExtension.Length]
            : Path.GetFileNameWithoutExtension(file);
        name = name.Trim();
        if (name.Length == 0) return null;
        return name.Length > 80 ? name[..80] : name;
    }

    private void Remember(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is not null) settings.Save(settings.Load() with { LastProfileFolder = folder });
    }
}