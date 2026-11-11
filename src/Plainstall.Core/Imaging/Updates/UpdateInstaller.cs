using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plainstall.Core.Imaging.Updates;

public enum UpdateStatus
{
    Installed,

    /// <summary>
    ///     DISM said the update does not apply to this image (already included, superseded, or for another edition).
    ///     Nothing changed.
    /// </summary>
    NotApplicable,
    Failed,

    /// <summary>Not tried, because an earlier update failed.</summary>
    NotAttempted
}

public sealed record UpdateOutcome(StagedUpdate Update, UpdateStatus Status, string? Reason = null)
{
    /// <summary>For the build log. A not-applicable update is skipped, not failed: the image is unchanged by it.</summary>
    public ActionOutcome ToActionOutcome()
    {
        return Status switch
        {
            UpdateStatus.Installed => ActionOutcome.Applied,
            UpdateStatus.NotApplicable => ActionOutcome.Skipped(Reason ?? "the update does not apply to this image"),
            UpdateStatus.NotAttempted => ActionOutcome.Skipped(Reason ?? "not tried, because an earlier update failed"),
            _ => ActionOutcome.Failed(Reason ?? "the update could not be added")
        };
    }
}

/// <summary>
///     The result of adding updates to one image. When <see cref="Succeeded" /> is false the image holds only
///     some of the chosen updates and must not be used: the pipeline should call <see cref="ThrowIfFailed" />,
///     which fails the build, and let its <c>finally</c> discard the mount.
/// </summary>
public sealed record UpdateInstallResult(
    IReadOnlyList<UpdateOutcome> Outcomes,
    bool ServicingPending,
    IReadOnlyList<string> Notes)
{
    /// <summary>The update that failed and stopped the sequence, or null.</summary>
    public UpdateOutcome? Failure => Outcomes.FirstOrDefault(o => o.Status == UpdateStatus.Failed);

    public bool Succeeded => Failure is null;

    /// <summary>Plain-English explanation of the failure, or null.</summary>
    public string? FailureMessage => Failure is not { } failure
        ? null
        : $"The update '{failure.Update.FileName}' (number {failure.Update.Number} of {Outcomes.Count}) could not be added: {failure.Reason} "
          + (Outcomes.Count(o => o.Status == UpdateStatus.NotAttempted) is var skipped and > 0
              ? $"The {skipped} update(s) after it were not tried, because they may depend on it. "
              : "")
          + "The build was stopped, because a Windows image with only some of the chosen updates cannot be trusted. "
          + "Check that the updates are for this Windows version and are listed in the right order (a servicing stack or checkpoint update before the update that needs it).";

    /// <summary>Throws a <see cref="PlainstallException" /> of kind <see cref="ErrorKind.Imaging" /> when an update failed.</summary>
    public void ThrowIfFailed()
    {
        if (FailureMessage is { } message) throw new PlainstallException(ErrorKind.Imaging, message);
    }
}

/// <summary>Progress while adding updates: which one, and DISM's percentage for it.</summary>
public readonly record struct UpdateInstallProgress(int Number, int Count, string FileName, int Percent)
{
    public int OverallPercent => Count == 0 ? 100 : Math.Clamp(((Number - 1) * 100 + Percent) / Count, 0, 100);
}

/// <summary>
///     Adds staged updates to a mounted install image one by one, in order, through
///     <see cref="IMountedImage.AddPackage" />. Unlike drivers, the first failure stops the sequence:
///     later updates may depend on the one that failed, and an image with a partial update set is not a
///     state anyone chose. An update DISM reports as not applicable (0x800F081E) changes nothing and is
///     recorded as skipped; the sequence goes on.
///     <para>
///         After the last update the image's package list is read again. A package in a pending state means
///         some servicing only completes when Windows first starts; component cleanup and file deletions must
///         then be skipped for this build (<see cref="UpdateInstallResult.ServicingPending" />).
///     </para>
///     <para>
///         What this does NOT do: no download, no dependency resolution, no reordering of servicing stack and
///         cumulative updates, no retry. It services the image it is given, which is meant to be the install
///         image; boot.wim and WinRE are not updated by Plainstall. It must run before Plainstall loads any
///         registry hive of the image, because DISM writes those hives itself.
///     </para>
/// </summary>
public sealed class UpdateInstaller
{
    /// <summary>CBS_E_NOT_APPLICABLE.</summary>
    internal const int NotApplicableHResult = unchecked((int)0x800F081E);

    /// <summary>CBS_E_NEW_SERVICING_STACK_REQUIRED.</summary>
    internal const int NewServicingStackRequiredHResult = unchecked((int)0x800F0823);

    private readonly IMountedImage _image;
    private readonly ILogger _logger;

    public UpdateInstaller(IMountedImage image, ILogger? logger = null)
    {
        _image = image;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    ///     Adds the updates in the order given. Never throws for a failing update; the result says what failed.
    ///     A cancel request is honoured between updates, never during one, and throws
    ///     <see cref="OperationCanceledException" />; the image then holds a partial set and must be discarded.
    /// </summary>
    public UpdateInstallResult Install(IReadOnlyList<StagedUpdate> updates,
        IProgress<UpdateInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<UpdateOutcome>(updates.Count);
        var notes = new List<string>();
        for (var i = 0; i < updates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var update = updates[i];
            var number = i + 1;
            progress?.Report(new UpdateInstallProgress(number, updates.Count, update.FileName, 0));
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Adding update {Number} of {Count}: {File}", number, updates.Count,
                    update.FileName);
            try
            {
                _image.AddPackage(update.StagedPath,
                    new InlineProgress<int>(p =>
                        progress?.Report(new UpdateInstallProgress(number, updates.Count, update.FileName,
                            Math.Clamp(p, 0, 100)))));
                outcomes.Add(new UpdateOutcome(update, UpdateStatus.Installed));
            }
            catch (Exception ex) when (ex is PlainstallException or IOException or UnauthorizedAccessException)
            {
                if (HasHResult(ex, NotApplicableHResult))
                {
                    var note =
                        $"The update '{update.FileName}' does not apply to this image and was skipped. It is probably already included in this version of Windows, or made for another version.";
                    _logger.LogWarning("{Note}", note);
                    notes.Add(note);
                    outcomes.Add(new UpdateOutcome(update, UpdateStatus.NotApplicable,
                        "the update does not apply to this image"));
                    continue;
                }

                var reason = ex.Message;
                if (HasHResult(ex, NewServicingStackRequiredHResult))
                    reason +=
                        " It needs a newer servicing stack: add the servicing stack update for this Windows version before it in the list.";
                _logger.LogError("Adding the update {File} failed: {Message}", update.FileName, ex.Message);
                outcomes.Add(new UpdateOutcome(update, UpdateStatus.Failed, reason));
                outcomes.AddRange(updates.Skip(i + 1).Select(u => new UpdateOutcome(u, UpdateStatus.NotAttempted,
                    $"not tried, because '{update.FileName}' failed before it")));
                // No pending check: the image is not going to be used.
                return new UpdateInstallResult(outcomes, false, notes);
            }
        }

        progress?.Report(new UpdateInstallProgress(updates.Count, updates.Count, "", 100));

        if (outcomes.All(o => o.Status != UpdateStatus.Installed))
            return new UpdateInstallResult(outcomes, false, notes);

        List<string> pending;
        try
        {
            pending = [.. _image.GetPackages().Where(p => p.State == ComponentState.Pending).Select(p => p.Name)];
        }
        catch (PlainstallException ex)
        {
            // Not knowing is treated as pending: skipping cleanup costs space, deleting needed files breaks Windows.
            _logger.LogWarning("Could not read the image's package list after adding updates: {Message}", ex.Message);
            notes.Add(
                "The list of installed updates could not be read back, so component cleanup and file deletions are skipped to be safe.");
            return new UpdateInstallResult(outcomes, true, notes);
        }

        if (pending.Count == 0) return new UpdateInstallResult(outcomes, false, notes);
        var pendingNote =
            $"{pending.Count} update package(s) finish installing when Windows first starts. Component cleanup and file deletions are skipped for this build. Nothing is wrong with the image.";
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("{Note} Pending: {Packages}", pendingNote, string.Join(", ", pending));
        notes.Add(pendingNote);
        return new UpdateInstallResult(outcomes, true, notes);
    }

    /// <summary>
    ///     The DISM error code travels as the HRESULT of an inner exception, and its hex form is part of the
    ///     message (<c>(0x800F081E)</c>). Both are checked so this works whatever wraps the DISM exception.
    /// </summary>
    internal static bool HasHResult(Exception ex, int hresult)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.HResult == hresult)
                return true;

        return ex.Message.Contains($"0x{hresult:X8}", StringComparison.OrdinalIgnoreCase);
    }
}