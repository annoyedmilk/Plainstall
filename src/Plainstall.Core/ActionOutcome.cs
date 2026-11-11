namespace Plainstall.Core;

public enum ActionStatus
{
    /// <summary>The change was made and, where possible, read back.</summary>
    Applied,

    /// <summary>Nothing was changed, for a reason that is not an error (the target is not in this image, for example).</summary>
    Skipped,

    /// <summary>The change could not be made.</summary>
    Failed
}

/// <summary>What happened to one planned action. Goes into the log and the build report.</summary>
public sealed record ActionOutcome(ActionStatus Status, string? Reason = null)
{
    public static readonly ActionOutcome Applied = new(ActionStatus.Applied);

    public static ActionOutcome AppliedWith(string note)
    {
        return new ActionOutcome(ActionStatus.Applied, note);
    }

    public static ActionOutcome Skipped(string reason)
    {
        return new ActionOutcome(ActionStatus.Skipped, reason);
    }

    public static ActionOutcome Failed(string reason)
    {
        return new ActionOutcome(ActionStatus.Failed, reason);
    }
}