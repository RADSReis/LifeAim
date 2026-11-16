using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Goals.Domain;

namespace LifeAim.Modules.Goals.Tests;

/// <summary>Funcionalidade 6: which changes of status are allowed, and what each one needs.</summary>
public class GoalLifecycleTests
{
    [Fact]
    public void An_active_goal_can_be_paused_ended_or_replaced_but_not_resumed()
    {
        Assert.Contains(GoalStatus.Active, GoalLifecycle.CanPause);
        Assert.Contains(GoalStatus.Active, GoalLifecycle.CanCancel);
        Assert.Contains(GoalStatus.Active, GoalLifecycle.CanReplace);
        Assert.DoesNotContain(GoalStatus.Active, GoalLifecycle.CanResume);
    }

    [Fact]
    public void A_paused_goal_can_come_back_change_its_day_or_be_ended_without_coming_back_first()
    {
        Assert.Contains(GoalStatus.Paused, GoalLifecycle.CanResume);
        Assert.Contains(GoalStatus.Paused, GoalLifecycle.CanPause);
        Assert.Contains(GoalStatus.Paused, GoalLifecycle.CanCancel);
        Assert.Contains(GoalStatus.Paused, GoalLifecycle.CanReplace);
    }

    [Theory]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Replaced)]
    public void An_ended_goal_is_closed_and_never_changes_again(GoalStatus status)
    {
        Assert.True(GoalLifecycle.IsClosed(status));
        Assert.DoesNotContain(status, GoalLifecycle.CanPause.Concat(GoalLifecycle.CanResume).Concat(GoalLifecycle.CanCancel).Concat(GoalLifecycle.CanReplace));
    }

    [Fact]
    public void A_pause_may_have_no_end_but_never_ends_in_the_past()
    {
        var today = new DateOnly(2026, 11, 16);

        Assert.Null(GoalLifecycle.ValidResumeOn(null, today));
        Assert.Equal(today, GoalLifecycle.ValidResumeOn(today, today));
        Assert.Throws<ArgumentException>(() => GoalLifecycle.ValidResumeOn(today.AddDays(-1), today));
    }

    [Fact]
    public void The_reason_is_optional_trimmed_and_capped()
    {
        Assert.Null(GoalLifecycle.ValidReason("   "));
        Assert.Equal("viagem", GoalLifecycle.ValidReason("  viagem "));
        Assert.Equal(GoalLifecycle.MaxReasonLength, GoalLifecycle.ValidReason(new string('a', 900))!.Length);
    }

    [Fact]
    public void Statuses_are_stored_as_lowercase_text()
    {
        Assert.Equal("replaced", GoalLifecycle.ToText(GoalStatus.Replaced));
        Assert.Equal(GoalStatus.Paused, GoalLifecycle.Parse("paused"));
    }
}
