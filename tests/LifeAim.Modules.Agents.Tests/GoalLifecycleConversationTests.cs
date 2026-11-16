using LifeAim.Modules.Agents.Application.Agent;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Pauses;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>Funcionalidade 6: pausing, ending or replacing a goal by conversation, never as a failure, keeping the history.</summary>
public class GoalLifecycleConversationTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateOnly Today = new(2026, 11, 17);

    private readonly MovingTime _time = new(At(Today, 11, 0));
    private readonly InMemoryConversationStore _store = new();
    private readonly FakeGoalService _goals = new();
    private readonly FakeProgressService _progress = new();
    private readonly FakeCheckInService _checkIns;
    private readonly FakePlanningService _planning = new();
    private readonly FakePippinChannel _channel = new();
    private readonly GoalDto _running;
    private readonly GoalDto _reading;

    public GoalLifecycleConversationTests()
    {
        _checkIns = new FakeCheckInService(_time);
        _store.States[UserId] = new ConversationState(Stage.CollectGoals, "Rafael");
        _running = _goals.Add(UserId, "Correr 5 km", DateTimeOffset.UtcNow);
        _reading = _goals.Add(UserId, "Ler 12 livros", DateTimeOffset.UtcNow);
        _goals.ReplaceRoutinesAsync(_running.Id, UserId, [new RoutineInput("correr", RoutineFrequency.Daily, RoutineSource.User)]).Wait();
    }

    [Fact]
    public async Task A_pause_takes_the_goal_out_of_the_follow_up_and_keeps_its_plan()
    {
        var ai = new FakeChatClient(
            FakeChatClient.ToolCall("pause_goal", new() { ["goalNumber"] = 1, ["resumeOn"] = "2026-12-01", ["reason"] = "machuquei o joelho" }),
            FakeChatClient.Text("Pausei **Correr 5 km** até 01/12. O histórico fica guardado."));

        var reply = await Workflow(ai, goalTalk: true).ReplyAsync(Message("machuquei o joelho, quero dar um tempo na corrida até dezembro"));

        Assert.StartsWith("Pausei **Correr 5 km**", reply);
        var paused = _goals.Lifecycle[_running.Id];
        Assert.Equal((GoalStatus.Paused, new DateOnly(2026, 12, 1), "machuquei o joelho"), (paused.Status, paused.ResumeOn, paused.StatusReason));
        Assert.Empty(await _goals.ListUserIdsWithRoutinesAsync()); // no follow-up while paused
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
        Assert.Single(_goals.RoutinesReplaced, r => r.GoalId == _running.Id); // the plan was not touched
    }

    [Fact]
    public async Task A_pause_cannot_end_in_the_past()
    {
        var tools = Tools("dá um tempo");

        var result = await tools.PauseGoalAsync(goalNumber: 1, resumeOn: "2026-11-10");

        Assert.StartsWith("Erro:", result);
        Assert.Empty(_goals.Lifecycle);
    }

    [Fact]
    public async Task Replacing_asks_the_explicit_yes_then_keeps_the_old_one_in_the_history_and_details_the_new_one()
    {
        var ask = FakeChatClient.ToolCall(
            "request_goal_replacement",
            new() { ["goalNumber"] = 1, ["newName"] = "Nadar 2x por semana", ["reason"] = "o joelho não aguenta corrida" });

        var question = await Workflow(new FakeChatClient(ask, FakeChatClient.Text("")), goalTalk: true)
            .ReplyAsync(Message("troca a corrida por natação, o joelho não aguenta"));

        Assert.Equal(ConfirmCancelGoalNode.ReplaceQuestion("Correr 5 km", "Nadar 2x por semana"), question);
        Assert.Equal(Stage.ConfirmCancelGoal, _store.States[UserId].Stage);
        Assert.Equal(GoalStatus.Active, _goals.StatusOf(_running.Id)); // nothing before the "sim"

        var done = await Workflow(new FakeChatClient(
                """{ "decision": "confirm" }""",
                """{ "question": "Quantas vezes por semana você consegue nadar?" }"""))
            .ReplyAsync(Message("sim"));

        Assert.StartsWith("Pronto, troquei **Correr 5 km** por **Nadar 2x por semana**!", done);
        Assert.EndsWith("Quantas vezes por semana você consegue nadar?", done);
        var old = _goals.Lifecycle[_running.Id];
        var swimming = _goals.Saved.Single(g => g.Name == "Nadar 2x por semana");
        Assert.Equal((GoalStatus.Replaced, swimming.Id, "o joelho não aguenta corrida"), (old.Status, old.ReplacedBy, old.StatusReason));
        Assert.Equal(Stage.DetailGoals, _store.States[UserId].Stage);
        Assert.Equal([swimming.Id], _store.States[UserId].Draft!.DetailQueue);
    }

    [Fact]
    public async Task Saying_no_to_the_replacement_changes_nothing()
    {
        await Workflow(new FakeChatClient(
                FakeChatClient.ToolCall("request_goal_replacement", new() { ["goalNumber"] = 1, ["newName"] = "Nadar" }),
                FakeChatClient.Text("")), goalTalk: true)
            .ReplyAsync(Message("troca a corrida por natação"));

        var reply = await Workflow(new FakeChatClient("""{ "decision": "keep" }""")).ReplyAsync(Message("não, deixa"));

        Assert.StartsWith("Combinado, nada muda", reply);
        Assert.Equal(GoalStatus.Active, _goals.StatusOf(_running.Id));
        Assert.Equal(2, _goals.Saved.Count);
        Assert.Null(_store.States[UserId].Draft!.Replacement);
    }

    [Fact]
    public async Task Ending_a_goal_keeps_the_reason_with_its_history()
    {
        await Workflow(new FakeChatClient(
                FakeChatClient.ToolCall("request_goal_cancellation", new() { ["goalNumber"] = 2, ["reason"] = "não faz mais sentido" }),
                FakeChatClient.Text("")), goalTalk: true)
            .ReplyAsync(Message("cancela a leitura, não faz mais sentido"));

        await Workflow(new FakeChatClient("""{ "decision": "confirm" }""")).ReplyAsync(Message("sim"));

        var ended = _goals.Lifecycle[_reading.Id];
        Assert.Equal((GoalStatus.Cancelled, "não faz mais sentido"), (ended.Status, ended.StatusReason));
        Assert.Equal([_running.Id], (await _goals.ListAsync(UserId)).Select(g => g.Id));
    }

    [Fact]
    public async Task A_paused_goal_can_be_ended_without_coming_back_first()
    {
        await _goals.PauseAsync(_reading.Id, UserId, null, null);

        await Workflow(new FakeChatClient(
                FakeChatClient.ToolCall("request_goal_cancellation", new() { ["pausedNumber"] = 1 }),
                FakeChatClient.Text("")), goalTalk: true)
            .ReplyAsync(Message("pode encerrar a leitura que estava pausada"));
        var reply = await Workflow(new FakeChatClient("""{ "decision": "confirm" }""")).ReplyAsync(Message("sim"));

        Assert.StartsWith("Pronto, cancelei a meta **Ler 12 livros**", reply);
        Assert.Equal(GoalStatus.Cancelled, _goals.StatusOf(_reading.Id));
    }

    [Fact]
    public async Task On_the_day_a_pause_ends_Pippin_asks_once_whether_it_comes_back()
    {
        await _goals.PauseAsync(_running.Id, UserId, Today, "viagem");

        await Job().RunAsync(CancellationToken.None);
        _store.States[UserId] = _store.States[UserId] with { Stage = Stage.CollectGoals };
        await Job().RunAsync(CancellationToken.None);

        var text = Assert.Single(_channel.Sent).Text;
        Assert.Contains("Hoje termina a pausa que você deu em **Correr 5 km**", text);
        Assert.Contains("O plano dela continua guardado", text);
        Assert.EndsWith(ResumeGoalNode.Question("Correr 5 km"), text);
        Assert.Contains(_running.Id, _goals.ResumeOffered);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(20)]
    public async Task Only_during_the_day(int hour)
    {
        await _goals.PauseAsync(_running.Id, UserId, Today, null);
        _time.Now = At(Today, hour, 0);

        await Job().RunAsync(CancellationToken.None);

        Assert.Empty(_channel.Sent);
    }

    [Fact]
    public async Task Never_on_top_of_something_in_progress()
    {
        await _goals.PauseAsync(_running.Id, UserId, Today, null);
        _store.States[UserId] = new ConversationState(Stage.CheckIn, "Rafael");

        await Job().RunAsync(CancellationToken.None);

        Assert.Empty(_channel.Sent);
        Assert.Empty(_goals.ResumeOffered); // asked later, when the user is free
    }

    [Fact]
    public async Task Yes_brings_the_goal_back_with_its_plan()
    {
        await _goals.PauseAsync(_running.Id, UserId, Today, null);
        await Job().RunAsync(CancellationToken.None);

        var reply = await Workflow(new FakeChatClient("""{ "decision": "resume" }""")).ReplyAsync(Message("bora!"));

        Assert.StartsWith("Que bom ter **Correr 5 km** de volta!", reply);
        Assert.Equal(GoalStatus.Active, _goals.StatusOf(_running.Id));
        Assert.Equal([UserId], await _goals.ListUserIdsWithRoutinesAsync()); // back in the follow-up
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task More_time_keeps_it_paused_until_the_new_day()
    {
        await _goals.PauseAsync(_running.Id, UserId, Today, null);
        await Job().RunAsync(CancellationToken.None);

        var reply = await Workflow(new FakeChatClient("""{ "decision": "more_time", "resumeOn": "2027-01-04" }"""))
            .ReplyAsync(Message("ainda não, só em janeiro"));

        Assert.Equal("Tudo bem, **Correr 5 km** continua pausada até 04/01; nesse dia eu pergunto de novo. 🙂", reply);
        Assert.Equal((GoalStatus.Paused, new DateOnly(2027, 1, 4)), (_goals.StatusOf(_running.Id), _goals.Lifecycle[_running.Id].ResumeOn));
        Assert.DoesNotContain(_running.Id, _goals.ResumeOffered);
    }

    [Fact]
    public async Task Not_wanting_it_any_more_goes_to_the_explicit_confirmation()
    {
        await _goals.PauseAsync(_running.Id, UserId, Today, null);
        await Job().RunAsync(CancellationToken.None);

        var question = await Workflow(new FakeChatClient("""{ "decision": "end", "reason": "perdi a vontade" }"""))
            .ReplyAsync(Message("acho que não quero mais correr"));

        Assert.Equal(ConfirmCancelGoalNode.Question("Correr 5 km"), question);
        Assert.Equal(GoalStatus.Paused, _goals.StatusOf(_running.Id)); // nothing before the "sim"

        await Workflow(new FakeChatClient("""{ "decision": "confirm" }""")).ReplyAsync(Message("sim"));

        Assert.Equal((GoalStatus.Cancelled, "perdi a vontade"), (_goals.StatusOf(_running.Id), _goals.Lifecycle[_running.Id].StatusReason));
    }

    [Fact]
    public async Task The_history_tells_the_path_even_without_the_goal_completed()
    {
        await _progress.RecordAsync(new Progress.Contracts.RecordProgressRequest(UserId, _running.Id, Today.AddDays(-3), "correu 5 km", 5, "km"));
        await _progress.RecordAsync(new Progress.Contracts.RecordProgressRequest(UserId, _running.Id, Today.AddDays(-1), "correu 3 km", 3, "km"));
        await _goals.PauseAsync(_running.Id, UserId, new DateOnly(2026, 12, 1), "joelho");
        await _goals.CancelAsync(_reading.Id, UserId, "sem tempo agora");

        var history = await Tools("como foi o caminho até aqui?").GoalHistoryAsync();

        Assert.Contains("Correr 5 km: pausada desde", history);
        Assert.Contains("volta em 01/12/2026 (motivo: joelho)", history);
        Assert.Contains("2 registro(s) de progresso, somando 8 km", history);
        Assert.Contains("Ler 12 livros: encerrada em", history);
        Assert.Contains("(motivo: sem tempo agora). Sem progresso registrado.", history);
    }

    [Fact]
    public async Task An_open_follow_up_of_a_goal_paused_meanwhile_is_not_asked_again()
    {
        var routine = (await _goals.ListRoutinesAsync(UserId)).Single();
        var checkIn = _checkIns.Add(UserId, _running.Id, routine.Id, Today, "correr", _time.Now);
        _store.States[UserId] = new ConversationState(Stage.CheckIn, "Rafael", GoalsDraft.Empty(null) with
        {
            CheckIns = [new CheckInItem(checkIn.Id, _running.Id, _running.Name, "correr", Today, null, null)],
        });
        await _goals.PauseAsync(_running.Id, UserId, null, null);

        var node = new CheckInNode(new FakeChatClient("not used"), _progress, _checkIns, _time);
        var context = new ConversationContext
        {
            UserId = UserId, DisplayName = "rafael", IncomingText = "oi", History = [], Stage = Stage.CheckIn,
            Draft = _store.States[UserId].Draft, SavedGoals = await _goals.ListAsync(UserId),
        };

        var result = await node.RunAsync(context, CancellationToken.None);

        Assert.False(result.Expected);
        Assert.Equal(Stage.CollectGoals, result.NextStage);
        Assert.Equal(Progress.Contracts.CheckInStatus.Asked, _checkIns.CheckIns.Single().Status); // unconfirmed, never a miss
    }

    private GoalTools Tools(string text) =>
        new(
            new ConversationContext
            {
                UserId = UserId, DisplayName = "rafael", IncomingText = text, History = [], Stage = Stage.CollectGoals,
                SavedGoals = _goals.ListAsync(UserId).Result,
            },
            _goals, _progress, _checkIns, _time);

    private PausedGoalsJob Job() => new(_goals, _store, Workflow(new FakeChatClient("not used")), _time, NullLogger<PausedGoalsJob>.Instance);

    // goalTalk: the message goes to the goals agent (the path classifier answers "goal_talk" first).
    private PippinWorkflow Workflow(FakeChatClient ai, bool goalTalk = false)
    {
        IChatClient chat = goalTalk ? new Prefixed(FakeChatClient.Text("""{ "route": "goal_talk" }"""), ai) : ai;
        var detailing = new GoalDetailing(chat, _goals, _planning, _time);
        return new PippinWorkflow(
            _store,
            [
                new GreetingNode(), new CollectGoalsNode(chat, _time), new DetailGoalsNode(chat, detailing, _time),
                new ConfirmCancelGoalNode(chat, _goals, detailing), new ResumeGoalNode(chat, _goals, _time),
                new CheckInNode(chat, _progress, _checkIns, _time),
            ],
            new FallbackRouter(),
            new GreetingReply(),
            new PathClassifier(chat),
            new GoalsAgent(chat, _goals, _progress, _checkIns, _time, NullLoggerFactory.Instance),
            _goals,
            _channel,
            NullLogger<PippinWorkflow>.Instance);
    }

    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.FromHours(-3));

    private static PippinMessage Message(string text) => new(UserId, "rafael", text);

    /// <summary>
    /// CollectGoals reads the message first (it says "not for me"), then the path classifier picks goal_talk, then the
    /// goals agent runs on <paramref name="rest"/>: the first two calls get fixed answers.
    /// </summary>
    private sealed class Prefixed(ChatMessage classifier, FakeChatClient rest) : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            ++_calls switch
            {
                1 => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    """{ "aboutGoals": false, "isGreeting": false, "goals": [], "doubts": [], "finished": false }"""))),
                2 => Task.FromResult(new ChatResponse(classifier)),
                _ => rest.GetResponseAsync(messages, options, cancellationToken),
            };

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class MovingTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
