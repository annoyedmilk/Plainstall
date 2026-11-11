namespace Plainstall.Core.Imaging;

/// <summary>A provisioned app package as DISM reports it.</summary>
/// <param name="DisplayName">The package name without version and publisher id, for example <c>Clipchamp.Clipchamp</c>.</param>
/// <param name="PackageName">The full name DISM needs to remove it.</param>
public sealed record ProvisionedPackage(string DisplayName, string PackageName);

/// <summary>State of a capability, feature or package, reduced to what the pipeline decides on.</summary>
public enum ComponentState
{
    NotPresent,
    Installed,

    /// <summary>A change is recorded but only takes effect when the image starts. Blocks cleanup and file deletion.</summary>
    Pending,

    /// <summary>Present in the image but not installed (staged, superseded).</summary>
    Other,

    /// <summary>Disabled, and its files were removed from the image, so it cannot be enabled offline.</summary>
    PayloadRemoved
}

public sealed record ComponentInfo(string Name, ComponentState State);

public enum CleanupResult
{
    Done,

    /// <summary>DISM refused because operations are pending (0x800F0806). Nothing was reverted; the image is just larger.</summary>
    SkippedPendingOperations
}

public sealed record MountedImageInfo(string MountPath, string ImageFilePath, int ImageIndex);

/// <summary>An image mounted for servicing. Disposing closes the servicing session but leaves the mount in place.</summary>
public interface IMountedImage : IDisposable
{
    /// <summary>The folder where the image's files appear.</summary>
    string MountPath { get; }

    IReadOnlyList<ProvisionedPackage> GetProvisionedPackages();

    void RemoveProvisionedPackage(string packageName);

    IReadOnlyList<ComponentInfo> GetCapabilities();

    void RemoveCapability(string capabilityName);

    IReadOnlyList<ComponentInfo> GetFeatures();

    void DisableFeature(string featureName);

    /// <summary>Enables a feature and the parent features it needs, from the files in the image only.</summary>
    void EnableFeature(string featureName);

    IReadOnlyList<ComponentInfo> GetPackages();

    void AddPackage(string packagePath, IProgress<int>? progress = null);

    void AddDriver(string infPath);

    CleanupResult CleanupComponents(bool resetBase, IProgress<int>? progress = null);
}

/// <summary>
///     The DISM operations Plainstall needs, behind an interface so that the pipeline and the action
///     executors can be tested with a fake. The real implementation needs an elevated process.
/// </summary>
public interface IImagingService
{
    /// <summary>Mounts one image of a WIM file read-write. The mount folder must exist and be empty.</summary>
    IMountedImage Mount(string wimPath, int index, string mountPath, IProgress<int>? progress = null);

    /// <summary>
    ///     Opens a new servicing session on an image that is already mounted. Needed after the first session
    ///     was closed, for example because DISM keeps the image's registry files open while a session lasts.
    /// </summary>
    IMountedImage Open(string mountPath);

    /// <summary>Unmounts, saving the changes into the WIM. Never interrupt this.</summary>
    void UnmountAndCommit(string mountPath, IProgress<int>? progress = null);

    /// <summary>Unmounts and throws the changes away.</summary>
    void UnmountAndDiscard(string mountPath, IProgress<int>? progress = null);

    /// <summary>Every image the system currently has mounted, by any tool.</summary>
    IReadOnlyList<MountedImageInfo> GetMountedImages();
}

/// <summary>The WIM operations DismApi does not have. Implemented with dism.exe.</summary>
public interface IWimTool
{
    /// <summary>
    ///     Exports one image into a new file with maximum WIM compression, so it can be mounted and split.
    ///     The destination must not exist: DISM appends to an existing file.
    /// </summary>
    Task ExportImageAsync(string sourceFile, int sourceIndex, string destinationFile,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Splits a WIM into .swm parts no larger than <paramref name="partSizeMb" />. Returns the files created.</summary>
    Task<IReadOnlyList<string>> SplitImageAsync(string wimFile, string swmFile, int partSizeMb,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}