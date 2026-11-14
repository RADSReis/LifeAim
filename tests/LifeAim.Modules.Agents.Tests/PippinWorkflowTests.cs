using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeAim.Modules.Agents.Tests;

public class PippinWorkflowTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();

    // 13/11/2026: between March and November, so Pippin asks "fim deste ano ou mês específico".
    private static readonly FixedTimeProvider November = new(new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.Zero));

    private readonly InMemoryConversationStore _store = new();

    [Fact]
    public async Task First_contact_introduces_pippin_and_moves_to_asking_the_name()
    {
        var workflow = CreateWorkflow(new FakeChatClient("unused"));

        var reply = await workflow.ReplyAsync(Message("oi"));

        Assert.Equal(GreetingNode.Question, reply);
        Assert.Equal(Stage.AskName, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task A_name_is_kept_and_pippin_asks_about_the_goals_horizon()
    {
        var ai = new FakeChatClient("""{ "answeredTheQuestion": true, "name": "Rafa" }""");
        var workflow = CreateWorkflow(ai);
        await workflow.ReplyAsync(Message("oi"));

        var reply = await workflow.ReplyAsync(Message("pode me chamar de Rafa"));

        Assert.StartsWith("Prazer, Rafa!", reply);
        Assert.Contains("fim deste ano ou para algum mês específico", reply);
        Assert.Equal(new ConversationState(Stage.CollectGoals, "Rafa"), _store.States[UserId]);

        // The node sent the AI its instructions and only the user's message.
        var call = Assert.Single(ai.Calls);
        Assert.Equal(ChatRole.System, call[0].Role);
        Assert.Equal("pode me chamar de Rafa", call[^1].Text);
    }

    [Fact]
    public async Task An_unexpected_message_goes_to_the_router_and_the_stage_stays()
    {
        var workflow = CreateWorkflow(new FakeChatClient("""{ "answeredTheQuestion": false, "name": null }"""));
        await workflow.ReplyAsync(Message("oi"));

        var reply = await workflow.ReplyAsync(Message("me conta uma piada"));

        Assert.Contains($"Mas voltando ao que eu tinha perguntado: {GreetingNode.Question}", reply);
        Assert.Equal(Stage.AskName, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Without_an_api_key_pippin_says_its_brain_is_off_and_waits_at_the_same_stage()
    {
        var workflow = CreateWorkflow(new MissingApiKeyChatClient());
        await workflow.ReplyAsync(Message("oi")); // the greeting needs no AI

        var reply = await workflow.ReplyAsync(Message("Rafael"));

        Assert.Contains("sem o meu cérebro de IA", reply);
        Assert.Equal(Stage.AskName, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Every_turn_is_saved_with_the_stage_it_happened_at()
    {
        var workflow = CreateWorkflow(new FakeChatClient("unused"));

        await workflow.ReplyAsync(Message("oi"));

        Assert.Equal(
            [(ConversationRole.User, "oi", Stage.Greeting), (ConversationRole.Assistant, GreetingNode.Question, Stage.Greeting)],
            _store.Messages.Select(m => (m.Turn.Role, m.Turn.Content, m.Stage)));
    }

    [Theory]
    [InlineData(1, "ao longo deste ano")]
    [InlineData(2, "ao longo deste ano")]
    [InlineData(6, "fim deste ano ou para algum mês específico")]
    [InlineData(12, "metas de ano novo")]
    public void The_goals_horizon_follows_the_month(int month, string expected)
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, month, 15, 15, 0, 0, TimeSpan.Zero));

        Assert.Contains(expected, GoalHorizon.Question(time));
    }

    private PippinWorkflow CreateWorkflow(IChatClient ai) =>
        new(
            _store,
            [new GreetingNode(), new AskNameNode(ai, November), new CollectGoalsNode()],
            new FallbackRouter(),
            NullLogger<PippinWorkflow>.Instance);

    private static PippinMessage Message(string text) => new(UserId, "rafael", text);
}
