using Plainstall.Core.Catalog;

namespace Plainstall.Core.Profiles;

/// <summary>Creates new profiles from a preset.</summary>
public static class ProfileFactory
{
    public static Profile FromPreset(Preset preset, string? name = null)
    {
        return new Profile
        {
            Schema = Profile.CurrentSchema,
            Name = name ?? preset.Title,
            Windows = new WindowsOptions
            {
                Source = WindowsSource.Download,
                Language = "auto",
                Edition = "Professional"
            },
            Apps = new AppSelection { Preset = preset.Id },
            Tweaks = new TweakSelection { Preset = preset.Id },
            Setup = new SetupOptions
            {
                LocalAccount = preset.Setup.LocalAccount ? new LocalAccountOptions { Name = null } : null,
                SkipPrivacyPages = preset.Setup.SkipPrivacyPages,
                BypassHardwareChecks = preset.Setup.BypassHardwareChecks
            }
        };
    }
}