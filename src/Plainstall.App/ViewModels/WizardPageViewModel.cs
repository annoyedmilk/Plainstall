using CommunityToolkit.Mvvm.ComponentModel;

namespace Plainstall.App.ViewModels;

public enum WizardStep
{
    Welcome,
    Windows,
    Customize,
    Review,
    Build,
    Done
}

/// <summary>Lets a page send the wizard to another step, for example "back to Customize to fix this".</summary>
public interface IWizardNavigator
{
    void GoTo(WizardStep step);
}

/// <summary>One step of the wizard.</summary>
public abstract class WizardPageViewModel : ObservableObject
{
    public abstract WizardStep Step { get; }

    /// <summary>Whether Next may be pressed. Pages raise PropertyChanged for it when it changes.</summary>
    public virtual bool CanContinue => true;

    /// <summary>Why Next is not available, in plain words, or null.</summary>
    public virtual string? ContinueBlockedReason => null;

    /// <summary>
    ///     The title of the section that Next opens on this same page, or null when Next goes to the next step.
    ///     Pages raise PropertyChanged for it when it changes.
    /// </summary>
    public virtual string? NextSectionTitle => null;

    /// <summary>Called every time the page is shown, so it can pick up changes made on other pages.</summary>
    public virtual Task OnShownAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>Opens the next section of the page. False when there is none, so Next goes to the next step.</summary>
    public virtual bool MoveToNextSection()
    {
        return false;
    }

    /// <summary>Opens the previous section of the page. False when there is none, so Back goes to the step before.</summary>
    public virtual bool MoveToPreviousSection()
    {
        return false;
    }

    /// <summary>Called before the wizard arrives with Next (true) or with Back (false).</summary>
    public virtual void PrepareArrival(bool forward)
    {
    }

    protected void NotifyCanContinue()
    {
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ContinueBlockedReason));
    }
}