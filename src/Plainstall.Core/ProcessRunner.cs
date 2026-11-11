using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Plainstall.Core;

/// <summary>A command line to run. Arguments are passed as a list and quoted by the runtime, never joined by hand.</summary>
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Called for every line (or progress-bar redraw) the process prints, on a background thread.</summary>
    public Action<string>? OnLine { get; init; }

    /// <summary>
    ///     What a cancel request does. False (the default) lets the process finish and then reports the
    ///     cancellation: DISM must never be killed in the middle of a mount, commit or export. True kills
    ///     the process tree, for tools that are safe to stop, such as oscdimg.
    /// </summary>
    public bool KillOnCancel { get; init; }
}

public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> Lines);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
///     Runs a console tool and collects its output. Standard output and standard error are drained at the
///     same time (a tool that fills one pipe while we wait on the other would hang), and a bare carriage
///     return counts as a line end, because DISM redraws its progress bar that way.
/// </summary>
public sealed partial class ProcessRunner : IProcessRunner
{
    static ProcessRunner()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding OemEncoding
    {
        get
        {
            try
            {
                return Encoding.GetEncoding((int)GetOEMCP());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.UTF8;
            }
        }
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        // DISM and oscdimg write in the OEM code page of the system when their output is redirected.
        var encoding = OemEncoding;
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            WorkingDirectory = request.WorkingDirectory ?? ""
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process();
        process.StartInfo = startInfo;
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new PlainstallException(ErrorKind.Internal, $"Could not start '{request.FileName}': {ex.Message}",
                ex);
        }

        var lines = new List<string>();
        var gate = new object();

        void Collect(string line)
        {
            lock (gate)
            {
                lines.Add(line);
            }

            request.OnLine?.Invoke(line);
        }

        var stdout = PumpAsync(process.StandardOutput, Collect);
        var stderr = PumpAsync(process.StandardError, Collect);

        var cancel = new CancelState(process, request.KillOnCancel);
        await using (cancellationToken.Register(static state => ((CancelState)state!).Cancel(), cancel)
                         .ConfigureAwait(false))
        {
            // Deliberately not passing the token: when KillOnCancel is false the process runs to its end.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

        if (cancel.Cancelled || cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        lock (gate)
        {
            return new ProcessResult(process.ExitCode, [.. lines]);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetOEMCP();

    /// <summary>Splits a stream into lines at LF, CR LF and bare CR, skipping empty lines.</summary>
    internal static async Task PumpAsync(TextReader reader, Action<string> onLine)
    {
        var buffer = new char[4096];
        var current = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                    Flush();
                else
                    current.Append(c);
            }

        Flush();
        return;

        void Flush()
        {
            if (current.Length == 0) return;
            var line = current.ToString().TrimEnd();
            current.Clear();
            if (line.Length > 0) onLine(line);
        }
    }

    /// <summary>What the cancellation callback needs. It is handed over as state, so the callback captures nothing.</summary>
    private sealed class CancelState(Process process, bool killOnCancel)
    {
        public bool Cancelled { get; private set; }

        public void Cancel()
        {
            Cancelled = true;
            if (!killOnCancel) return;
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }
    }
}