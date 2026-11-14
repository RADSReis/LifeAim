using LifeAim.Modules.Goals.Domain;

namespace LifeAim.Modules.Goals.Tests;

public class GoalTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 20, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void Only_the_name_is_required()
    {
        var goal = Goal.Create(UserId, "Ler 12 livros no ano", null, null, null, Now);

        Assert.Equal("Ler 12 livros no ano", goal.Name);
        Assert.Null(goal.Description);
        Assert.Null(goal.Indicator);
        Assert.Null(goal.DueDate);
        Assert.Equal(UserId, goal.UserId);
    }

    [Fact]
    public void Trims_the_text_and_treats_blank_optional_fields_as_unknown()
    {
        var goal = Goal.Create(UserId, "  Ir à academia  ", "   ", " 3 vezes por semana ", null, Now);

        Assert.Equal("Ir à academia", goal.Name);
        Assert.Null(goal.Description);
        Assert.Equal("3 vezes por semana", goal.Indicator);
    }

    [Fact]
    public void Stores_the_creation_time_in_utc()
    {
        var goal = Goal.Create(UserId, "Pesar 90 kg", null, null, new DateOnly(2026, 12, 31), Now);

        Assert.Equal(TimeSpan.Zero, goal.CreatedAt.Offset);
        Assert.Equal(Now, goal.CreatedAt);
    }

    [Fact]
    public void Keeps_the_start_and_target_values_with_their_unit()
    {
        var goal = Goal.Create(
            UserId, "Emagrecer até 90 kg", null, "peso corporal", new DateOnly(2026, 12, 25), Now,
            startValue: 94m, targetValue: 90m, unit: " kg ");

        Assert.Equal(94m, goal.StartValue);
        Assert.Equal(90m, goal.TargetValue);
        Assert.Equal("kg", goal.Unit);
    }

    [Fact]
    public void Rejects_a_unit_longer_than_the_limit()
    {
        var unit = new string('k', Goal.MaxUnitLength + 1);

        Assert.Throws<ArgumentException>(() => Goal.Create(UserId, "Correr", null, null, null, Now, unit: unit));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_goal_without_a_name(string name)
    {
        Assert.Throws<ArgumentException>(() => Goal.Create(UserId, name, null, null, null, Now));
    }

    [Fact]
    public void Rejects_a_name_longer_than_the_limit()
    {
        var name = new string('a', Goal.MaxNameLength + 1);

        Assert.Throws<ArgumentException>(() => Goal.Create(UserId, name, null, null, null, Now));
    }

    [Fact]
    public void Rejects_a_goal_without_a_user()
    {
        Assert.Throws<ArgumentException>(() => Goal.Create(Guid.Empty, "Aprender inglês", null, null, null, Now));
    }

    [Fact]
    public void The_creation_time_has_the_databases_precision_microseconds()
    {
        var now = new DateTimeOffset(2026, 11, 14, 15, 2, 48, TimeSpan.Zero).AddTicks(5481404);

        var goal = Goal.Create(Guid.CreateVersion7(), "Ler mais", null, null, null, now);

        Assert.Equal(now.AddTicks(-4), goal.CreatedAt);
    }

    [Fact]
    public void Changing_a_goal_keeps_its_identity_and_follows_the_same_rules()
    {
        var goal = Goal.Create(Guid.CreateVersion7(), "Ler mais", null, null, null, DateTimeOffset.UtcNow);

        var changed = goal.With(" Ler 2 livros ", null, "livros terminados", new DateOnly(2026, 12, 31), null, 2m, "livros");

        Assert.Equal((goal.Id, goal.UserId, goal.CreatedAt), (changed.Id, changed.UserId, changed.CreatedAt));
        Assert.Equal("Ler 2 livros", changed.Name);
        Assert.Throws<ArgumentException>(() => goal.With(" ", null, null, null, null, null, null));
    }
}
