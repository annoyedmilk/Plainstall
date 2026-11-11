using System.Globalization;
using Microsoft.Extensions.Logging;
using Plainstall.Core.Catalog;
using Plainstall.Core.Download;
using Plainstall.Core.Imaging;
using Plainstall.Core.Media;
using Plainstall.Core.Output;
using Plainstall.Core.Output.Usb;
using Plainstall.Core.Profiles;
using Plainstall.Core.Registry;
using Plainstall.Core.Unattend;

namespace Plainstall.Core.Pipeline;

// The seams of the pipeline. Each has one real implementation and a fake in the tests, so the stage
// order, the journal and the cleanup paths can be tested without DISM, admin rights or an ISO.

/// <summary>The registry hives of one mounted image, loaded for the lifetime of the session.</summary>
public interface IHiveSession : IDisposable
{
    RegistryRoot? Root(RegistryHiveId hive);
}

public interface IHiveSessionFactory
{
    IHiveSession Open(string imageRoot, IReadOnlyCollection<RegistryHiveId> hives, BuildJournal journal,
        ILogger logger);
}

/// <summary>Loads the hive files of a mounted image under HKLM. Needs an elevated process.</summary>
public sealed class OfflineHiveSessionFactory : IHiveSessionFactory
{
    public IHiveSession Open(string imageRoot, IReadOnlyCollection<RegistryHiveId> hives, BuildJournal journal,
        ILogger logger)
    {
        var session = new Session();
        try
        {
            foreach (var hive in hives)
            {
                var file = OfflineHive.HiveFilePath(imageRoot, hive);
                session.Add(hive,
                    OfflineHive.Load(file, OfflineHive.MountNameFor(journal.SessionId, hive), journal, logger));
            }

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private sealed class Session : IHiveSession
    {
        private readonly Dictionary<RegistryHiveId, OfflineHive> _hives = [];

        public RegistryRoot? Root(RegistryHiveId hive)
        {
            return _hives.GetValueOrDefault(hive)?.Root;
        }

        public void Dispose()
        {
            // Unload every hive even if one refuses. Report the first refusal afterwards.
            Exception? first = null;
            foreach (var hive in _hives.Values)
                try
                {
                    hive.Dispose();
                }
                catch (PlainstallException ex)
                {
                    first ??= ex;
                }

            _hives.Clear();
            if (first is not null) throw first;
        }

        public void Add(RegistryHiveId id, OfflineHive hive)
        {
            _hives[id] = hive;
        }
    }
}

/// <summary>Installation media that can be read: a mounted ISO or a folder.</summary>
public interface IMediaHandle : IAsyncDisposable
{
    string RootPath { get; }
}

public interface IMediaOpener
{
    Task<IMediaHandle> OpenAsync(string isoOrFolder, ILogger logger, CancellationToken cancellationToken);
}

/// <summary>Attaches an ISO read-only, or uses a folder with extracted media as it is.</summary>
public sealed class MediaOpener : IMediaOpener
{
    public async Task<IMediaHandle> OpenAsync(string isoOrFolder, ILogger logger, CancellationToken cancellationToken)
    {
        if (Directory.Exists(isoOrFolder)) return new FolderHandle(Path.GetFullPath(isoOrFolder));
        return File.Exists(isoOrFolder)
            ? new IsoHandle(await IsoMount.AttachAsync(isoOrFolder, logger, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            : throw new PlainstallException(ErrorKind.InvalidInput,
                $"'{isoOrFolder}' was not found. Give the path of a Windows 11 ISO file.");
    }

    private sealed class FolderHandle(string root) : IMediaHandle
    {
        public string RootPath => root;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IsoHandle(IsoMount mount) : IMediaHandle
    {
        public string RootPath => mount.RootPath;

        public ValueTask DisposeAsync()
        {
            return mount.DisposeAsync();
        }
    }
}

/// <summary>Where the Windows ISO for a build comes from.</summary>
/// <param name="Kind">"iso" for a file the user supplied, "download" for one fetched from Microsoft.</param>
public sealed record SourceIso(string Path, string Kind, string? Sha256, bool VerifiedAgainstMicrosoft);

public interface ISourceProvider
{
    /// <summary>The folder the ISO is downloaded into, or null when this source downloads nothing.</summary>
    string? DownloadFolder { get; }

    Task<SourceIso> GetAsync(Profile profile, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>An ISO (or media folder) the user already has.</summary>
public sealed class GivenSource(string path) : ISourceProvider
{
    public string? DownloadFolder => null;

    public Task<SourceIso> GetAsync(Profile profile, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new SourceIso(Path.GetFullPath(path), "iso", null, false));
    }
}

/// <summary>The current ISO from Microsoft, from the cache when it is still the published one.</summary>
public sealed class DownloadSource(WindowsIsoProvider provider) : ISourceProvider
{
    public string DownloadFolder => provider.CacheFolder;

    public async Task<SourceIso> GetAsync(Profile profile, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        // "auto" is the display language of this PC. Anything else is a language tag such as de-DE.
        var culture = profile.Windows.Language == "auto"
            ? CultureInfo.CurrentUICulture
            : CultureInfo.GetCultureInfo(profile.Windows.Language);
        var relay = progress is null ? null : new RelayProgress(progress);
        var result = await provider.GetIsoAsync(culture, relay, cancellationToken).ConfigureAwait(false);
        return new SourceIso(result.Path, "download", result.Sha256, result.VerifiedAgainstMicrosoft);
    }

    private sealed class RelayProgress(IProgress<TransferProgress> target) : IProgress<WindowsIsoProgress>
    {
        public void Report(WindowsIsoProgress value)
        {
            if (value.Transfer is { } transfer) target.Report(transfer);
        }
    }
}

/// <summary>Finds or fetches oscdimg. A seam so tests do not touch the network.</summary>
public interface IOscdimgProvider
{
    Task<string> GetPathAsync(CancellationToken cancellationToken);
}

public sealed class OscdimgProvider(OscdimgLocator locator) : IOscdimgProvider
{
    public Task<string> GetPathAsync(CancellationToken cancellationToken)
    {
        return locator.LocateAsync(cancellationToken);
    }
}

/// <summary>Everything a build needs from outside. The CLI and the app fill it with the real implementations.</summary>
public sealed record PipelineServices
{
    public required Catalog.Catalog Catalog { get; init; }
    public required ISourceProvider Source { get; init; }
    public required IMediaOpener MediaOpener { get; init; }

    /// <summary>Created per build, because DISM needs the log and scratch folders of that build.</summary>
    public required Func<WorkLayout, IImagingService> ImagingFactory { get; init; }

    public required Func<WorkLayout, IWimTool> WimToolFactory { get; init; }
    public required IHiveSessionFactory Hives { get; init; }
    public required IOscdimgProvider Oscdimg { get; init; }
    public required IProcessRunner ProcessRunner { get; init; }

    /// <summary>
    ///     Makes the USB writer for one build, from that build's image tool and journal. Null when this host
    ///     cannot write USB sticks. A request that names a stick is then refused before anything starts.
    /// </summary>
    public Func<IWimTool, IUsbJournal, IUsbWriter>? UsbWriterFactory { get; init; }

    public required Func<HostFacts> HostFacts { get; init; }
    public required Func<string, HostEnvironment> HostEnvironment { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
}