namespace Plainstall.Core.Output.Usb;

/// <summary>
///     Decides whether a disk may be erased. Pure: it looks only at the <see cref="DiskFacts" /> it is given,
///     so every rule is tested without a disk, and the same rules run when the picker is filled and again
///     immediately before the erase.
/// </summary>
public static class UsbDiskRules
{
    /// <summary>
    ///     Smallest disk offered at all. Microsoft names a stick sold as "8 GB" as the minimum for Windows 11
    ///     media, and such sticks hold a little under 8 billion bytes (7.2 to 8.0 billion is common), so the line
    ///     is drawn at 7 billion: anything below that is a 4 GB stick or smaller. Current media is often larger
    ///     than 8 GB. Then the check against the real size of the media
    ///     (<see cref="UsbSelectionContext.RequiredBytes" />) refuses a stick that is too small.
    /// </summary>
    public const long MinimumDiskBytes = 7_000_000_000;

    /// <summary>
    ///     Largest disk offered: 256 GiB, a little above what a "256 GB" stick holds. Larger removable drives
    ///     are almost always backup drives, and erasing one by mistake would be a disaster.
    /// </summary>
    public const long MaximumDiskBytes = 256L * 1024 * 1024 * 1024;

    /// <summary>
    ///     Checks every rule and collects the reason for each one that fails, so the app can tell the user
    ///     everything that is wrong with a disk at once.
    /// </summary>
    public static UsbCandidate Evaluate(DiskFacts disk, UsbSelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(context);
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(disk.BusType))
            reasons.Add(
                "Windows did not say how this disk is connected, so Plainstall cannot be sure it is a USB stick.");
        else if (!IsUsb(disk.BusType))
            reasons.Add(
                $"This disk is not connected by USB (Windows reports {disk.BusType.Trim()}). Plainstall only writes to USB sticks.");

        if (!disk.IsRemovableMedia && !context.AllowExternalDrives)
            reasons.Add("Windows reports this disk as a hard drive or SSD, not as a USB stick or memory card. " +
                        "Such drives often contain backups or other important files. " +
                        "Plainstall erases this drive only if you confirm that it is a USB stick.");

        if (disk.IsBoot) reasons.Add("This is the disk Windows runs from.");
        if (disk.IsSystem) reasons.Add("This disk holds the files your PC needs to start Windows.");
        if (disk.HoldsWindowsFiles)
            reasons.Add("Windows is using this disk for its page file, crash dump or hibernation file.");
        if (disk.IsOffline) reasons.Add("This disk is offline in Windows, so it cannot be written.");
        if (disk.IsReadOnly)
            reasons.Add("This disk is write-protected (read-only). If the stick or card has a small lock switch, " +
                        "slide it to the unlocked position and plug it in again.");
        if (string.IsNullOrWhiteSpace(disk.UniqueId))
            reasons.Add(
                "Windows does not report a unique id for this disk, so Plainstall could not make sure it erases exactly this one.");

        if (disk.SizeBytes <= 0)
        {
            reasons.Add("There is no memory card in this card reader, or Windows cannot read the stick.");
        }
        else
        {
            switch (disk.SizeBytes)
            {
                case < MinimumDiskBytes:
                    reasons.Add(
                        $"This stick is too small: it holds {UsbSizes.Format(disk.SizeBytes)}, and Windows 11 needs a stick of at least 8 GB.");
                    break;
                case > MaximumDiskBytes:
                    reasons.Add($"This drive holds {UsbSizes.Format(disk.SizeBytes)}, which is larger than 256 GB. " +
                                "Drives this large are usually backup drives, so Plainstall does not offer them.");
                    break;
            }

            var usable = UsbSizes.UsableBytesOnDisk(disk.SizeBytes);
            if (context.RequiredBytes > usable)
                reasons.Add($"The installation files need {UsbSizes.Format(context.RequiredBytes)}, " +
                            $"but this stick has room for only {UsbSizes.Format(usable)}.");
        }

        foreach (var path in context.PathsInUse.Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var volume = disk.Volumes.FirstOrDefault(v => UsbPaths.IsOnVolume(path, v));
            if (volume is null) continue;
            var where = volume.DriveLetter is { Length: > 0 } letter ? $" (drive {letter.TrimEnd('\\')})" : "";
            reasons.Add(
                $"This disk holds '{path}'{where}, which this build is using. Erasing the disk would destroy it.");
        }

        return new UsbCandidate(disk, reasons.Count == 0, reasons);
    }

    /// <summary>
    ///     Everything that tells the confirmed disk apart from the disk read just now, in words for a person.
    ///     Number, size, unique id and serial number decide, with the serial rules of
    ///     <see cref="DiskIdentity.DifferencesFrom" />; a confirmed disk without a unique id never matches,
    ///     because without one a swapped stick of the same model and size would pass. The model name and the
    ///     bus are compared too: they never change for the same device.
    /// </summary>
    public static IReadOnlyList<string> Differences(DiskFacts confirmed, DiskFacts current)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(current);
        var differences = new List<string>();
        if (DiskIdentity.Clean(confirmed.UniqueId) is null)
            differences.Add(
                "Windows did not report a unique id for the disk you confirmed, so it cannot be recognised again");
        else
            differences.AddRange(DiskIdentity.Of(confirmed).DifferencesFrom(current.Number, current.UniqueId,
                current.SerialNumber, current.SizeBytes));
        if (!string.Equals(confirmed.FriendlyName.Trim(), current.FriendlyName.Trim(),
                StringComparison.OrdinalIgnoreCase))
            differences.Add($"its name is now '{current.FriendlyName}' instead of '{confirmed.FriendlyName}'");
        if (!string.Equals(confirmed.BusType.Trim(), current.BusType.Trim(), StringComparison.OrdinalIgnoreCase))
            differences.Add($"it is now connected by {current.BusType} instead of {confirmed.BusType}");
        return differences;
    }

    internal static bool IsUsb(string? busType)
    {
        return string.Equals(busType?.Trim(), "USB", StringComparison.OrdinalIgnoreCase);
    }
}