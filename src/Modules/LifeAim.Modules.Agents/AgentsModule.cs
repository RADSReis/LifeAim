using LifeAim.BuildingBlocks;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Agents.Infrastructure;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAI.Chat;

namespace LifeAim.Modules.Agents;

public static class AgentsModule
{
    /// <summary>The "agents" schema and its embedded SQL scripts, for LifeAim.Migrator.</summary>
    public static ModuleMigrations Migrations { get; } = new("agents", typeof(AgentsModule).Assembly);

    public static IServiceCollection AddAgentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        // The AI. Code depends only on IChatClient (Microsoft.Extensions.AI); OpenAI is one line here,
        // so another provider means changing this line, not the nodes.
        var openAI = configuration.GetSection(OpenAIOptions.SectionName).Get<OpenAIOptions>() ?? new OpenAIOptions();
        if (string.IsNullOrWhiteSpace(openAI.ApiKey))
        {
            services.AddSingleton<IChatClient, MissingApiKeyChatClient>();
        }
        else
        {
            services.AddChatClient(new ChatClient(openAI.Model, openAI.ApiKey).AsIChatClient())
                .UseLogging();
        }

        // The workflow: its context store, its nodes (one per stage) and the router.
        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<INode, GreetingNode>();
        services.AddSingleton<INode, AskNameNode>();
        services.AddSingleton<INode, CollectGoalsNode>();
        services.AddSingleton<FallbackRouter>();
        services.AddSingleton<IPippin, PippinWorkflow>();

        return services;
    }
}
