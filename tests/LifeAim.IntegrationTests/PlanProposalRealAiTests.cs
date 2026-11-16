using LifeAim.Modules.Agents.Application.Planning;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Planning.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit.Abstractions;

namespace LifeAim.IntegrationTests;

/// <summary>
/// Funcionalidade 2 with the real model: the job writes the proposal of two goals, then the user goes through it
/// in the chat until one goal's plan is saved. Checks the rules, not the words.
/// Run with <c>$env:LIFEAIM_REAL_AI=1; dotnet test --filter Category=RealAI</c>.
/// </summary>
[Trait("Category", "RealAI")]
public sealed class PlanProposalRealAiTests(RealAiApiFactory factory, ITestOutputHelper output)
    : IClassFixture<RealAiApiFactory>, IAsyncLifetime
{
    private readonly Guid _userId = Guid.CreateVersion7();

    private IGoalService Goals => factory.Services.GetRequiredService<IGoalService>();
    private IPlanningService Planning => factory.Services.GetRequiredService<IPlanningService>();
    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    [RealAiFact]
    public async Task The_job_writes_a_proposal_and_the_user_refines_one_goal_until_it_is_saved()
    {
        var books = await Goals.CreateAsync(new CreateGoalRequest(
            _userId, "Ler 2 livros", "livros de umas 200 páginas", "livros terminados", new DateOnly(2026, 12, 31),
            0, 2, "livros"));
        await Goals.CreateAsync(new CreateGoalRequest(
            _userId, "Ir à academia 3 vezes por semana", Indicator: "idas à academia por semana",
            DueDate: new DateOnly(2026, 12, 31), TargetValue: 3, Unit: "vezes"));
        await Planning.RequestAsync(_userId);

        // The background part, as Quartz would run it.
        await ActivatorUtilities.CreateInstance<PlanProposalsJob>(factory.Services).RunAsync(CancellationToken.None);

        var proposal = await Planning.GetOpenAsync(_userId);
        Assert.NotNull(proposal);
        Assert.Equal(2, proposal.Goals.Count);
        foreach (var goal in proposal.Goals)
        {
            output.WriteLine($"{goal.Message}\n  ? {goal.Question}\n  {goal.Routines.Count} routines\n");
            Assert.NotEmpty(goal.Routines);
            Assert.DoesNotContain(goal.Routines, r => r.TimeOfDay is not null); // no exact time the user never said
            Assert.EndsWith("?", goal.Question.Trim());
        }

        // The invitation is not sent in tests (no Discord): put the conversation where it would be.
        await AtOfferAsync(proposal.Id);

        var choices = await Send("sim, quero ver");
        Assert.Contains("Ler 2 livros", choices);
        Assert.Contains("academia", choices, StringComparison.OrdinalIgnoreCase);

        var focus = await Send("vamos começar pela leitura");
        Assert.Contains("?", focus);

        var adjusted = await Send("eu só consigo ler à noite, nos dias úteis");
        Assert.Contains(PlanReview.ConfirmQuestion, adjusted);

        await Send("sim");
        var routines = (await Goals.ListRoutinesAsync(_userId)).Where(r => r.GoalId == books.Id).ToList();
        Assert.NotEmpty(routines);
        Assert.All(routines, r => Assert.Equal(DayPeriod.Night, r.DayPeriod));
        Assert.Contains(GoalProposalStatus.Accepted, (await Planning.GetOpenAsync(_userId))!.Goals.Select(g => g.Status));
    }

    private async Task AtOfferAsync(Guid proposalId)
    {
        await using var command = Db.CreateCommand(
            """
            INSERT INTO agents.conversations (user_id, stage, user_name, updated_at, draft)
            VALUES ($1, 'OfferPlan', 'Teste', now(), $2)
            """);
        command.Parameters.Add(new() { Value = _userId });
        command.Parameters.Add(new()
        {
            Value = $$"""{ "horizon": null, "goals": [], "doubts": [], "proposalId": "{{proposalId}}" }""",
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> Send(string text)
    {
        var reply = await factory.Services.GetRequiredService<IPippin>().ReplyAsync(new PippinMessage(_userId, "teste", text));
        output.WriteLine($"> {text}\n{reply}\n");
        return reply;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await factory.Services.GetRequiredService<IConversationReset>().ResetAsync(_userId);
        foreach (var sql in new[]
                 {
                     "DELETE FROM planning.goal_proposals WHERE proposal_id IN (SELECT id FROM planning.proposals WHERE user_id = $1)",
                     "DELETE FROM planning.proposals WHERE user_id = $1",
                     "DELETE FROM goals.goals WHERE user_id = $1",
                 })
        {
            await using var command = Db.CreateCommand(sql);
            command.Parameters.Add(new() { Value = _userId });
            await command.ExecuteNonQueryAsync();
        }
    }
}
