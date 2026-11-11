using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plainstall.Core.Catalog;

namespace Plainstall.Core.Imaging;

/// <summary>
///     Applies the catalog actions that work on a mounted image through DISM or the file system:
///     app, capability and feature removal, feature enabling, and file deletion. Like the registry executor it reports an
///     outcome per action and does not throw for a single failure.
/// </summary>
public sealed class ImageActionExecutor
{
    private readonly IMountedImage _image;
    private readonly ILogger _logger;
    private IReadOnlyList<ProvisionedPackage>? _packages;

    public ImageActionExecutor(IMountedImage image, ILogger? logger = null)
    {
        _image = image;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    ///     Set when DISM recorded a change that only completes when the image first starts. Component cleanup
    ///     and file deletion must then be skipped: the pending work may still need those files.
    /// </summary>
    public bool ServicingPending { get; private set; }

    public ActionOutcome Apply(CatalogAction action)
    {
        try
        {
            return action switch
            {
                AppxRemoveProvisionedAction appx => RemoveApp(appx),
                CapabilityRemoveAction capability => RemoveCapability(capability),
                FeatureDisableAction feature => DisableFeature(feature),
                FeatureEnableAction feature => EnableFeature(feature),
                FileDeleteAction file => DeleteFile(file),
                _ => ActionOutcome.Failed($"{action.KindName} is not an image action.")
            };
        }
        catch (PlainstallException ex)
        {
            _logger.LogWarning("{Kind} {Target}: {Message}", action.KindName, action.Target, ex.Message);
            return ActionOutcome.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{Kind} {Target} failed", action.KindName, action.Target);
            return ActionOutcome.Failed(ex.Message);
        }
    }

    /// <summary>Records that servicing is pending, as found by a step outside this class (adding updates).</summary>
    public void MarkServicingPending()
    {
        ServicingPending = true;
    }

    /// <summary>Re-reads the state of updates added to the image and records whether any is pending.</summary>
    public void NotePendingPackages()
    {
        if (_image.GetPackages().Any(p => p.State == ComponentState.Pending)) ServicingPending = true;
    }

    private ActionOutcome RemoveApp(AppxRemoveProvisionedAction action)
    {
        _packages ??= _image.GetProvisionedPackages();
        // Exact name, never a substring: "Microsoft.Paint" must not catch "Microsoft.PaintSomethingElse".
        var matches = _packages
            .Where(p => string.Equals(p.DisplayName, action.Package, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return ActionOutcome.Skipped("not present in this image");
        foreach (var package in matches) _image.RemoveProvisionedPackage(package.PackageName);

        // A DISM call that returns is not proof. Read the list again.
        _packages = _image.GetProvisionedPackages();
        return _packages.Any(p => string.Equals(p.DisplayName, action.Package, StringComparison.OrdinalIgnoreCase))
            ? ActionOutcome.Failed("the package is still listed after removal")
            : ActionOutcome.Applied;
    }

    private ActionOutcome RemoveCapability(CapabilityRemoveAction action)
    {
        var matches = _image.GetCapabilities().Where(c => IsCapability(c.Name, action.Name)).ToList();
        var present = matches.Where(c => c.State != ComponentState.NotPresent).ToList();
        if (present.Count == 0) return ActionOutcome.Skipped("not present in this image");
        foreach (var capability in present) _image.RemoveCapability(capability.Name);

        var after = _image.GetCapabilities().Where(c => IsCapability(c.Name, action.Name)).ToList();
        if (after.Any(c => c.State == ComponentState.Installed))
            return ActionOutcome.Failed("the capability is still installed after removal");
        return after.Any(c => c.State == ComponentState.Pending)
            ? Pending("the removal completes when Windows first starts")
            : ActionOutcome.Applied;
    }

    /// <summary>Records that servicing is pending and returns an applied outcome with the given note.</summary>
    private ActionOutcome Pending(string note)
    {
        ServicingPending = true;
        return ActionOutcome.AppliedWith(note);
    }

    /// <summary>
    ///     A capability's full name is the base name followed by <c>~~~~version</c>. A name that continues
    ///     with a dot after the base name also matches.
    /// </summary>
    private static bool IsCapability(string fullName, string baseName)
    {
        return fullName.Equals(baseName, StringComparison.OrdinalIgnoreCase)
               || (fullName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) &&
                   fullName.Length > baseName.Length && fullName[baseName.Length] is '~' or '.');
    }

    private ActionOutcome DisableFeature(FeatureDisableAction action)
    {
        var feature = FindFeature(action.Name);
        if (feature is null) return ActionOutcome.Skipped("not present in this image");
        if (feature.State is not (ComponentState.Installed or ComponentState.Pending))
            return ActionOutcome.Skipped("already disabled");
        _image.DisableFeature(feature.Name);

        return FindFeature(action.Name)?.State switch
        {
            ComponentState.Installed => ActionOutcome.Failed("the feature is still enabled after disabling it"),
            ComponentState.Pending => Pending("the change completes when Windows first starts"),
            _ => ActionOutcome.Applied
        };
    }

    private ActionOutcome EnableFeature(FeatureEnableAction action)
    {
        var feature = FindFeature(action.Name);
        switch (feature?.State)
        {
            case null or ComponentState.NotPresent:
                return ActionOutcome.Skipped("not present in this image");
            case ComponentState.Installed:
                return ActionOutcome.Skipped("already enabled");
            case ComponentState.PayloadRemoved:
                return ActionOutcome.Skipped("its files were removed from this image, so it cannot be enabled");
        }

        _image.EnableFeature(feature.Name);

        return FindFeature(action.Name)?.State switch
        {
            ComponentState.Installed => ActionOutcome.Applied,
            ComponentState.Pending => Pending("the change completes when Windows first starts"),
            _ => ActionOutcome.Failed("the feature is still disabled after enabling it")
        };
    }

    private ComponentInfo? FindFeature(string name)
    {
        return _image.GetFeatures().FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private ActionOutcome DeleteFile(FileDeleteAction action)
    {
        if (ServicingPending)
            return ActionOutcome.Skipped(
                "servicing operations are pending in this image, and they may still need this file");
        var path = ImageFileDeleter.ResolveInside(_image.MountPath, action.Path);
        if (Directory.Exists(path))
        {
            if (!action.Recurse)
                return ActionOutcome.Failed("this is a folder, so the catalog entry needs \"recurse\": true");
            ImageFileDeleter.DeleteDirectory(path);
            return Directory.Exists(path)
                ? ActionOutcome.Failed("the folder is still there after deleting it")
                : ActionOutcome.Applied;
        }

        if (!ImageFileDeleter.DeleteFile(path)) return ActionOutcome.Skipped("not present in this image");
        return File.Exists(path)
            ? ActionOutcome.Failed("the file is still there after deleting it")
            : ActionOutcome.Applied;
    }
}