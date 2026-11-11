using Plainstall.Core.Imaging;

namespace Plainstall.Core.Tests.Imaging;

/// <summary>An in-memory stand-in for a mounted image. Records what was asked of it.</summary>
internal sealed class FakeMountedImage : IMountedImage
{
    public FakeMountedImage(string mountPath)
    {
        MountPath = mountPath;
    }

    public List<ProvisionedPackage> Packages { get; } = [];
    public List<ComponentInfo> Capabilities { get; } = [];
    public List<ComponentInfo> Features { get; } = [];
    public List<ComponentInfo> Updates { get; } = [];
    public List<string> Calls { get; } = [];

    /// <summary>Package names whose removal silently does nothing, as DISM sometimes does.</summary>
    public HashSet<string> StubbornPackages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Capabilities that go to "pending" instead of disappearing.</summary>
    public HashSet<string> DeferredCapabilities { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Features that go to "pending" instead of "installed" when they are enabled.</summary>
    public HashSet<string> DeferredFeatures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Features whose enabling silently does nothing.</summary>
    public HashSet<string> StubbornFeatures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Disposed { get; private set; }

    /// <summary>A new servicing session on the same image.</summary>
    public void Reopen()
    {
        Disposed = false;
    }

    /// <summary>Runs once, during the first app removal. Tests use it to cancel in the middle of servicing.</summary>
    public Action? OnFirstCall { get; set; }

    /// <summary>File names of updates DISM refuses to add.</summary>
    public HashSet<string> FailingPackages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File names of updates DISM reports as not applicable (0x800F081E).</summary>
    public HashSet<string> NotApplicablePackages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File names of updates that stay pending until Windows first starts.</summary>
    public HashSet<string> PendingPackages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>INF file names DISM refuses to add.</summary>
    public HashSet<string> RejectedDrivers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs once, before the first update or driver is added.</summary>
    public Action? OnFirstAdd { get; set; }

    public string MountPath { get; }

    public IReadOnlyList<ProvisionedPackage> GetProvisionedPackages()
    {
        return [.. Packages];
    }

    public void RemoveProvisionedPackage(string packageName)
    {
        var hook = OnFirstCall;
        OnFirstCall = null;
        hook?.Invoke();
        Calls.Add("remove-app " + packageName);
        if (!StubbornPackages.Contains(packageName)) Packages.RemoveAll(p => p.PackageName == packageName);
    }

    public IReadOnlyList<ComponentInfo> GetCapabilities()
    {
        return [.. Capabilities];
    }

    public void RemoveCapability(string capabilityName)
    {
        Calls.Add("remove-capability " + capabilityName);
        var index = Capabilities.FindIndex(c => c.Name == capabilityName);
        Capabilities[index] = Capabilities[index] with
        {
            State = DeferredCapabilities.Contains(capabilityName)
                ? ComponentState.Pending
                : ComponentState.NotPresent
        };
    }

    public IReadOnlyList<ComponentInfo> GetFeatures()
    {
        return [.. Features];
    }

    public void DisableFeature(string featureName)
    {
        Calls.Add("disable-feature " + featureName);
        var index = Features.FindIndex(f => f.Name == featureName);
        Features[index] = Features[index] with { State = ComponentState.Other };
    }

    public void EnableFeature(string featureName)
    {
        Calls.Add("enable-feature " + featureName);
        if (StubbornFeatures.Contains(featureName)) return;
        var index = Features.FindIndex(f => f.Name == featureName);
        Features[index] = Features[index] with
        {
            State = DeferredFeatures.Contains(featureName) ? ComponentState.Pending : ComponentState.Installed
        };
    }

    public IReadOnlyList<ComponentInfo> GetPackages()
    {
        return [.. Updates];
    }

    public void AddPackage(string packagePath, IProgress<int>? progress = null)
    {
        BeforeAdd();
        var name = Path.GetFileName(packagePath);
        if (FailingPackages.Contains(name))
        {
            Calls.Add("add-package-failed " + name);
            throw new PlainstallException(ErrorKind.Imaging,
                $"DISM could not add the update {name}: An error occurred. (0x800F0922)");
        }

        if (NotApplicablePackages.Contains(name))
        {
            Calls.Add("add-package-not-applicable " + name);
            // As DismImagingService wraps it: the DISM error as the inner exception, its HRESULT on it.
            throw new PlainstallException(ErrorKind.Imaging, $"DISM could not add the update {name}: not applicable",
                new InvalidOperationException("The specified package is not applicable to this image.")
                    { HResult = unchecked((int)0x800F081E) });
        }

        progress?.Report(50);
        progress?.Report(100);
        Calls.Add("add-package " + name);
        Updates.Add(new ComponentInfo(Path.GetFileNameWithoutExtension(packagePath),
            PendingPackages.Contains(name) ? ComponentState.Pending : ComponentState.Installed));
    }

    public void AddDriver(string infPath)
    {
        BeforeAdd();
        var name = Path.GetFileName(infPath);
        if (RejectedDrivers.Contains(name))
        {
            Calls.Add("add-driver-failed " + name);
            throw new PlainstallException(ErrorKind.Imaging,
                $"DISM could not add the driver {name}: The INF is bad. (0x800F0203)");
        }

        Calls.Add("add-driver " + name);
    }

    public CleanupResult CleanupComponents(bool resetBase, IProgress<int>? progress = null)
    {
        Calls.Add("cleanup" + (resetBase ? " resetbase" : ""));
        return CleanupResult.Done;
    }

    public void Dispose()
    {
        Disposed = true;
    }

    public void AddApp(string displayName, string arch = "x64")
    {
        Packages.Add(new ProvisionedPackage(displayName, $"{displayName}_1.0.0.0_{arch}__8wekyb3d8bbwe"));
    }

    private void BeforeAdd()
    {
        var hook = OnFirstAdd;
        OnFirstAdd = null;
        hook?.Invoke();
    }
}