using LifeAim.Modules.Agents.Application.Workflow;

namespace LifeAim.Modules.Agents.Tests;

public sealed class ConversationResetTests
{
    [Fact]
    public async Task Reset_forgets_state_and_messages_of_that_user_only()
    {
        var store = new InMemoryConversationStore();
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        await store.SaveTurnAsync(user, new ConversationState(Stage.CollectGoals, "Rafael"), "oi", "olá", Stage.Greeting, default);
        await store.SaveTurnAsync(other, new ConversationState(Stage.AskName, null), "oi", "olá", Stage.Greeting, default);

        var reset = await new ConversationReset(store).ResetAsync(user);

        Assert.True(reset);
        Assert.Equal(Stage.Greeting, (await store.LoadAsync(user, default)).Stage);
        Assert.Empty(await store.RecentMessagesAsync(user, 10, default));
        Assert.Equal(Stage.AskName, (await store.LoadAsync(other, default)).Stage);
    }

    [Fact]
    public async Task Reset_of_unknown_user_returns_false()
    {
        Assert.False(await new ConversationReset(new InMemoryConversationStore()).ResetAsync(Guid.NewGuid()));
    }
}
