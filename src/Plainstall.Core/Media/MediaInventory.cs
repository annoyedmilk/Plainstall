using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Media;

/// <summary>What Plainstall needs to know about Windows installation media before it plans a build.</summary>
public sealed record MediaInfo
{
    /// <summary>The architecture of every image Plainstall accepts, as reports and messages name it.</summary>
    public const string ArchitectureName = "x64";

    /// <summary><c>sources\install.wim</c> or <c>sources\install.esd</c>.</summary>
    public required WimInfo InstallImage { get; init; }

    /// <summary><c>sources\boot.wim</c>.</summary>
    public required WimInfo BootImage { get; init; }

    /// <summary>The editions on the media, ordered by index. Same list as <c>InstallImage.Images</c>.</summary>
    public IReadOnlyList<WimImageInfo> Editions => InstallImage.Images;

    /// <summary>Default language of the first edition, for example "en-US". Empty when the image names none.</summary>
    public required string Language { get; init; }

    /// <summary>Version of the first edition, for example 10.0.26300.9457.</summary>
    public required Version Version { get; init; }

    /// <summary>Build number of the install image, for example 26300.</summary>
    public int Build => Version.Build;

    /// <summary>
    ///     The image of boot.wim that runs Windows Setup. This is the one that receives setup-time
    ///     registry values and storage drivers; the other image is the plain recovery environment.
    /// </summary>
    public required WimImageInfo SetupImage { get; init; }

    /// <summary>True when <c>boot\etfsboot.com</c> exists, the boot sector an ISO needs to start on a legacy BIOS.</summary>
    public required bool HasBiosBootLoader { get; init; }

    /// <summary>Size of all files on the media, for free-space checks.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>
    ///     Finds an edition by its language-neutral id, for example "Professional". The display name is
    ///     never compared, because it is translated ("Windows 11 Pro" exists in 38 languages).
    /// </summary>
    /// <exception cref="PlainstallException">The media has no such edition; the message lists the ones it has.</exception>
    public WimImageInfo SelectEdition(string editionId)
    {
        ArgumentNullException.ThrowIfNull(editionId);
        var wanted = editionId.Trim();
        foreach (var edition in Editions)
            if (edition.EditionId.Length > 0 &&
                string.Equals(edition.EditionId, wanted, StringComparison.OrdinalIgnoreCase))
                return edition;

        var available = string.Join(", ", Editions.Select(e => $"{e.EditionId} ({e.Name})"));
        throw new PlainstallException(ErrorKind.InvalidInput,
            $"This Windows media has no edition '{wanted}'. Editions on this media: {available}. " +
            "Choose one of these edition ids in your profile, or use other media.");
    }
}

/// <summary>
///     Looks at the root of Windows installation media (an extracted folder or a mounted ISO) and
///     reports what is on it, reading image metadata straight from the WIM files. Nothing is mounted
///     or changed, and no admin rights are needed.
/// </summary>
public static class MediaInventory
{
    /// <summary>The oldest build Plainstall accepts: Windows 11 version 22H2.</summary>
    public const int MinimumBuild = 22621;

    public const string InstallWimPath = @"sources\install.wim";
    public const string InstallEsdPath = @"sources\install.esd";
    public const string InstallSwmPath = @"sources\install.swm";
    public const string BootWimPath = @"sources\boot.wim";
    public const string BiosBootLoaderPath = @"boot\etfsboot.com";
    public const string EfiBootImagePath = @"efi\microsoft\boot\efisys.bin";
    public const string EfiBootImageNoPromptPath = @"efi\microsoft\boot\efisys_noprompt.bin";

    private const string UseOfficialIso = "Use an unmodified Windows 11 ISO downloaded from Microsoft.";

    /// <summary>Reads and validates the media at <paramref name="rootPath" />.</summary>
    /// <exception cref="PlainstallException">
    ///     The folder is not Windows media, an image file is damaged, or the media is of a kind
    ///     Plainstall does not work with. The message says which and what to do.
    /// </exception>
    public static MediaInfo Read(string rootPath, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        logger ??= NullLogger.Instance;

        if (!Directory.Exists(rootPath))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The Windows media folder '{rootPath}' does not exist or cannot be read.");

        var installPath = FindInstallImage(rootPath);
        var install = WimMetadataReader.Read(installPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (install.IsSplit) throw SplitMedia();
        if (install.Images.Count == 0)
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The install image '{installPath}' contains no Windows edition. {UseOfficialIso}");

        ValidateEditions(install);

        var bootPath = Path.Combine(rootPath, BootWimPath);
        if (!File.Exists(bootPath))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"This Windows media is incomplete: '{BootWimPath}' is missing, so a PC could not start Windows Setup from it. {UseOfficialIso}");
        var boot = WimMetadataReader.Read(bootPath);
        if (boot.IsSplit || boot.Images.Count == 0)
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"This Windows media is incomplete: '{BootWimPath}' contains no usable boot image. {UseOfficialIso}");
        var setupImage = FindSetupImage(boot);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(Path.Combine(rootPath, EfiBootImagePath)) &&
            !File.Exists(Path.Combine(rootPath, EfiBootImageNoPromptPath)))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"This Windows media is incomplete: the EFI boot image '{EfiBootImagePath}' is missing, so the result could not be made bootable. {UseOfficialIso}");

        var first = install.Images[0];
        var info = new MediaInfo
        {
            InstallImage = install,
            BootImage = boot,
            Language = first.DefaultLanguage ?? (first.Languages.Count > 0 ? first.Languages[0] : ""),
            Version = first.Version!,
            SetupImage = setupImage,
            HasBiosBootLoader = File.Exists(Path.Combine(rootPath, BiosBootLoaderPath)),
            TotalBytes = MeasureTotalBytes(rootPath, cancellationToken)
        };

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Media at {Root}: {EditionCount} edition(s), {Architecture}, version {Version}, language {Language}, install image {Compression}, Setup image {SetupIndex} (build {BootBuild}), {TotalBytes} bytes",
                rootPath, info.Editions.Count, MediaInfo.ArchitectureName, info.Version, info.Language,
                install.Compression,
                setupImage.Index, setupImage.Build, info.TotalBytes);
        if (!ServicingFamily.AreSame(info.Build, setupImage.Build))
            // Not an error, only worth a line: it shows up when someone combined files from two releases.
            logger.LogWarning(
                "The Setup image (build {BootBuild}) and the install image (build {InstallBuild}) come from different Windows releases",
                setupImage.Build, info.Build);
        return info;
    }

    /// <summary>
    ///     The image of a boot.wim that runs Windows Setup: the first whose name contains "Setup"; when
    ///     no name says so, image 2 (Microsoft's layout: 1 = Windows PE, 2 = Setup); else the only image.
    /// </summary>
    /// <remarks>The name is checked first because a rebuilt boot.wim may hold its images in another order.</remarks>
    private static WimImageInfo FindSetupImage(WimInfo bootImage)
    {
        var images = bootImage.Images;
        return images.FirstOrDefault(i => i.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
               ?? images.FirstOrDefault(i => i.Index == 2)
               ?? images[0];
    }

    private static string FindInstallImage(string rootPath)
    {
        var wim = Path.Combine(rootPath, InstallWimPath);
        if (File.Exists(wim)) return wim;
        var esd = Path.Combine(rootPath, InstallEsdPath);
        if (File.Exists(esd)) return esd;
        if (File.Exists(Path.Combine(rootPath, InstallSwmPath))) throw SplitMedia();
        throw new PlainstallException(ErrorKind.InvalidInput,
            $"'{rootPath}' does not look like Windows installation media: there is no '{InstallWimPath}' and no '{InstallEsdPath}'. {UseOfficialIso}");
    }

    private static PlainstallException SplitMedia()
    {
        return new PlainstallException(ErrorKind.InvalidInput,
            "This Windows media stores its install image in several parts (install.swm), as on a USB stick made for FAT32. " +
            $"Split media is not supported as a source. {UseOfficialIso}");
    }

    /// <summary>Checks that every edition is desktop Windows 11 for x64.</summary>
    private static void ValidateEditions(WimInfo install)
    {
        foreach (var image in install.Images)
        {
            var label = image.Name.Length > 0 ? $"'{image.Name}'" : $"number {image.Index}";

            if (!string.Equals(image.InstallationType, "Client", StringComparison.OrdinalIgnoreCase))
            {
                var kind = image.InstallationType.Length > 0
                    ? $"installation type '{image.InstallationType}'"
                    : "no installation type";
                throw new PlainstallException(ErrorKind.InvalidInput,
                    $"Image {label} on this media is not a desktop edition of Windows ({kind}). " +
                    $"Plainstall works with Windows 11 for PCs only, not with Windows Server or other images. {UseOfficialIso}");
            }

            if (image.Version is not { } version)
                throw new PlainstallException(ErrorKind.InvalidInput,
                    $"Image {label} on this media does not state its Windows version, so Plainstall cannot tell whether it is Windows 11. {UseOfficialIso}");
            if (version.Major != 10 || version.Build < MinimumBuild)
                throw new PlainstallException(ErrorKind.InvalidInput,
                    $"This media contains Windows version {version.Major}.{version.Minor} build {version.Build}, which is not Windows 11 version 22H2 or newer. " +
                    $"Plainstall needs build {MinimumBuild} or newer. Download a current Windows 11 ISO from Microsoft.");

            if (!image.IsX64)
                throw new PlainstallException(ErrorKind.InvalidInput,
                    $"This media is for {image.ArchitectureName} processors. Plainstall works with Windows 11 for x64 PCs (Intel and AMD) only. " +
                    "Download the Windows 11 ISO for x64 devices from Microsoft.");
        }
    }

    private static long MeasureTotalBytes(string rootPath, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None
        };
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(rootPath).EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += file.Length;
            }
        }
        catch (IOException ex)
        {
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"Could not read all files of the Windows media at '{rootPath}': {ex.Message}", ex);
        }

        return total;
    }
}