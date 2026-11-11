namespace Plainstall.Core;

/// <summary>What went wrong, in terms a host can map to an exit code or a user message.</summary>
public enum ErrorKind
{
    /// <summary>A profile, catalog file or command-line value is not valid.</summary>
    InvalidInput,

    /// <summary>The PC is not ready for a build: no admin rights, too little space, wrong file system.</summary>
    Preflight,

    /// <summary>A network request failed for a reason that may go away (timeout, reset, server busy).</summary>
    Network,

    /// <summary>Microsoft's service answered, and the answer was "no" (blocked region, changed page).</summary>
    ServiceRefused,

    /// <summary>A hash or signature did not match what was expected.</summary>
    Verification,

    /// <summary>DISM or a WIM operation failed.</summary>
    Imaging,

    /// <summary>An offline registry hive could not be loaded, written or unloaded.</summary>
    Registry,

    /// <summary>Writing the USB stick or the ISO failed.</summary>
    Output,

    /// <summary>The user cancelled.</summary>
    Cancelled,

    /// <summary>A bug in Plainstall.</summary>
    Internal
}

/// <summary>
///     The one exception type Plainstall throws on purpose. The message is written for the person
///     using the tool: it says what happened and, where possible, what to do next.
/// </summary>
public class PlainstallException : Exception
{
    public PlainstallException(ErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public ErrorKind Kind { get; }

    /// <summary>An exception with a heading line and one indented "- " line for each problem.</summary>
    public static PlainstallException WithList(ErrorKind kind, string heading, IEnumerable<string> problems)
    {
        return new PlainstallException(kind,
            heading + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }
}