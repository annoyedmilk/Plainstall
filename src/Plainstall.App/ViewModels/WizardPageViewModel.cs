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

    /// <summary>Called every time the page is shown, so it can pick up changes made on other pages.</summary>
    public virtual Task OnShownAsync()
    {
        return Task.CompletedTask;
    }

    protected void NotifyCanContinue()
    {
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ContinueBlockedReason));
    }
}