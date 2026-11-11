namespace Plainstall.Core.Output.Usb;

/// <summary>
///     Fills the USB picker: every disk on the USB bus, eligible or not, each with the reasons from
///     <see cref="UsbDiskRules" />. Disks on other buses are left out entirely; nobody means their internal SSD.
/// </summary>
public sealed class UsbDiskService : IUsbDiskService
{
    private readonly IDiskOperations _disks;

    public UsbDiskService(IDiskOperations disks)
    {
        _disks = disks ?? throw new ArgumentNullException(nameof(disks));
    }

    public async Task<IReadOnlyList<UsbCandidate>> ListAsync(UsbSelectionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var disks = await _disks.GetDisksAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. disks
                .Where(d => UsbDiskRules.IsUsb(d.BusType))
                .OrderBy(d => d.Number)
                .Select(d => UsbDiskRules.Evaluate(d, context))
        ];
    }

    public Task<DiskFacts?> GetAsync(int diskNumber, CancellationToken cancellationToken = default)
    {
        return _disks.GetDiskAsync(diskNumber, cancellationToken);
    }
}