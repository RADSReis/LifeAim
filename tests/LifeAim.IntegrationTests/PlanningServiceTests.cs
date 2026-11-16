using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Planning.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeAim.IntegrationTests;

/// <summary>The Planning module's public API against a real, migrated PostgreSQL.</summary>
public sealed class PlanningServiceTests(LifeAimApiFactory factory) : IClassFixture<LifeAimApiFactory>, IAsyncLifetime
{
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _books = Guid.CreateVersion7();
    private readonly Guid _gym = Guid.CreateVersion7();

    private IPlanningService Planning => factory.Services.GetRequiredService<IPlanningService>();

    [Fact]
    public async Task A_proposal_goes_from_requested_to_ready_with_its_goals_and_routines()
    {
        Assert.True(await Planning.RequestAsync(_userId));
        Assert.False(await Planning.RequestAsync(_userId)); // one waiting is enough

        var requested = await OwnAsync(ProposalStatus.Requested);
        var routine = new RoutineInput(
            "ler", RoutineFrequency.Weekly, RoutineSource.Pippin, DaysOfWeek: [6, 7], DayPeriod: DayPeriod.Night,
            Amount: 30, Unit: "páginas");
        await Planning.MarkReadyAsync(requested.Id,
        [
            new(_gym, "Academia 3x por semana.", "Que dias?", []),
            new(_books, "30 páginas no fim de semana.", "Prefere à noite?", [routine]),
        ]);

        var open = await Planning.GetOpenAsync(_userId);

        Assert.NotNull(open);
        Assert.Equal(ProposalStatus.Ready, open.Status);
        Assert.Equal([_gym, _books], open.Goals.Select(g => g.GoalId));
        Assert.Equal([1, 2], open.Goals.Select(g => g.Position));
        var books = open.Goals[1];
        Assert.Equal(GoalProposalStatus.Open, books.Status);
        Assert.Equal(routine with { DaysOfWeek = null }, Assert.Single(books.Routines) with { DaysOfWeek = null });
        Assert.Equal([6, 7], books.Routines[0].DaysOfWeek);
    }

    [Fact]
    public async Task Accepted_goals_are_remembered_and_a_new_request_closes_the_open_proposal()
    {
        await Planning.RequestAsync(_userId);
        var first = await OwnAsync(ProposalStatus.Requested);
        await Planning.MarkReadyAsync(first.Id, [new(_books, "Ler.", "Quando?", []), new(_gym, "Treinar.", "Quando?", [])]);
        await Planning.MarkOfferedAsync(first.Id);
        await Planning.SetGoalStatusAsync(first.Id, _books, GoalProposalStatus.Accepted);
        await Planning.SetGoalStatusAsync(first.Id, _gym, GoalProposalStatus.Skipped);

        Assert.True(await Planning.RequestAsync(_userId));

        Assert.Equal([_books], await Planning.AcceptedGoalIdsAsync(_userId));
        Assert.Null(await Planning.GetOpenAsync(_userId)); // the first was closed, the new one is only requested
        await OwnAsync(ProposalStatus.Requested);
    }

    [Fact]
    public async Task Failed_attempts_are_counted_until_the_proposal_is_given_up()
    {
        await Planning.RequestAsync(_userId);
        var proposal = await OwnAsync(ProposalStatus.Requested);

        for (var i = 0; i < IPlanningService.MaxAttempts; i++)
        {
            await Planning.MarkFailedAttemptAsync(proposal.Id, "the AI answer could not be read");
        }

        var failed = await OwnAsync(ProposalStatus.Failed);
        Assert.Equal(IPlanningService.MaxAttempts, failed.Attempts);
    }

    [Fact]
    public async Task A_proposal_without_goals_is_just_closed()
    {
        await Planning.RequestAsync(_userId);
        var proposal = await OwnAsync(ProposalStatus.Requested);

        await Planning.MarkReadyAsync(proposal.Id, []);

        await OwnAsync(ProposalStatus.Closed);
        Assert.Null(await Planning.GetOpenAsync(_userId));
    }

    private async Task<PlanProposalDto> OwnAsync(ProposalStatus status) =>
        Assert.Single(await Planning.ListByStatusAsync(status, 100), p => p.UserId == _userId);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = dataSource.CreateCommand(
            """
            WITH goals AS (
                DELETE FROM planning.goal_proposals
                WHERE proposal_id IN (SELECT id FROM planning.proposals WHERE user_id = $1)
            )
            DELETE FROM planning.proposals WHERE user_id = $1
            """);
        command.Parameters.Add(new() { Value = _userId });
        await command.ExecuteNonQueryAsync();
    }
}
