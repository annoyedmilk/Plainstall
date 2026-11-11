using Plainstall.Core.Imaging.Updates;

namespace Plainstall.Core.Tests.Imaging.Updates;

public sealed class UpdateInstallerTests
{
    private readonly FakeMountedImage _image = new(@"C:\mount");

    private static IReadOnlyList<StagedUpdate> Staged(params string[] names)
    {
        return
        [
            .. names.Select((n, i) => new StagedUpdate(i + 1, i + 1, @"C:\src\" + n,
                $@"C:\work\updates\{i + 1:000}\{n}", 10_000, new string('A', 64), []))
        ];
    }

    [Fact]
    public void All_are_installed_in_order_with_progress()
    {
        var progress = new List<UpdateInstallProgress>();

        var result = new UpdateInstaller(_image).Install(Staged("ssu.msu", "lcu.msu", "net.cab"),
            new InlineProgress<UpdateInstallProgress>(progress.Add), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureMessage);
        result.ThrowIfFailed();
        Assert.Equal(["add-package ssu.msu", "add-package lcu.msu", "add-package net.cab"], _image.Calls);
        Assert.False(result.ServicingPending);
        Assert.All(result.Outcomes, o => Assert.Equal(ActionStatus.Applied, o.ToActionOutcome().Status));
        var overall = progress.Select(p => p.OverallPercent).ToList();
        Assert.Equal(overall.Order(), overall);
        Assert.Equal(100, overall[^1]);
        Assert.Contains(progress, p => p is { Number: 2, Percent: 50 });
    }

    [Fact]
    public void When_the_second_of_three_fails_the_third_is_not_attempted()
    {
        _image.FailingPackages.Add("lcu.msu");

        var result = new UpdateInstaller(_image).Install(Staged("ssu.msu", "lcu.msu", "net.cab"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(["add-package ssu.msu", "add-package-failed lcu.msu"], _image.Calls);
        Assert.Equal([UpdateStatus.Installed, UpdateStatus.Failed, UpdateStatus.NotAttempted],
            result.Outcomes.Select(o => o.Status));
        Assert.Equal("lcu.msu", result.Failure!.Update.FileName);
        Assert.Contains("lcu.msu", result.FailureMessage);
        Assert.Contains("number 2 of 3", result.FailureMessage);
        Assert.Contains("cannot be trusted", result.FailureMessage);
        Assert.Contains("1 update(s) after it were not tried", result.FailureMessage);
        var ex = Assert.Throws<PlainstallException>(result.ThrowIfFailed);
        Assert.Equal(ErrorKind.Imaging, ex.Kind);
    }

    [Fact]
    public void A_pending_package_is_detected_after_the_last_update()
    {
        _image.PendingPackages.Add("lcu.msu");

        var result = new UpdateInstaller(_image).Install(Staged("ssu.msu", "lcu.msu"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.ServicingPending);
        Assert.Contains(result.Notes, n => n.Contains("first starts"));
    }

    [Fact]
    public void A_not_applicable_update_is_skipped_and_the_sequence_continues()
    {
        _image.NotApplicablePackages.Add("old.msu");

        var result = new UpdateInstaller(_image).Install(Staged("old.msu", "lcu.msu"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal([UpdateStatus.NotApplicable, UpdateStatus.Installed], result.Outcomes.Select(o => o.Status));
        Assert.Equal(ActionStatus.Skipped, result.Outcomes[0].ToActionOutcome().Status);
        Assert.Contains(result.Notes, n => n.Contains("does not apply"));
    }

    [Fact]
    public void Nothing_to_install_is_a_success()
    {
        var result = new UpdateInstaller(_image).Install([], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.False(result.ServicingPending);
        Assert.Empty(_image.Calls);
    }

    [Fact]
    public void The_hresult_is_found_in_the_message_when_no_inner_exception_carries_it()
    {
        Assert.True(UpdateInstaller.HasHResult(
            new PlainstallException(ErrorKind.Imaging, "DISM could not add the update x: nope (0x800F081E)"),
            unchecked((int)0x800F081E)));
        Assert.False(UpdateInstaller.HasHResult(new PlainstallException(ErrorKind.Imaging, "other (0x800F0922)"),
            unchecked((int)0x800F081E)));
    }
}