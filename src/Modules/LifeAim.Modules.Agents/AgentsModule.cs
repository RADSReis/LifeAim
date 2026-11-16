using LifeAim.BuildingBlocks;
using LifeAim.Modules.Agents.Application.Agent;
using LifeAim.Modules.Agents.Application.FollowUp;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Planning;
using LifeAim.Modules.Agents.Application.Reports;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Agents.Infrastructure;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAI.Chat;
using Quartz;

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
        services.AddSingleton<INode, ConfirmGoalsNode>();
        services.AddSingleton<INode, DetailGoalsNode>();
        services.AddSingleton<INode, ConfirmCancelGoalNode>();
        services.AddSingleton<INode, OfferPlanNode>();
        services.AddSingleton<INode, ChoosePlanGoalNode>();
        services.AddSingleton<INode, RefinePlanGoalNode>();
        services.AddSingleton<INode, CheckInNode>();
        services.AddSingleton<INode, SummaryInsightNode>();
        services.AddSingleton<GoalDetailing>();
        services.AddSingleton<FallbackRouter>();
        services.AddSingleton<GreetingReply>();
        services.AddSingleton<PathClassifier>();

        // The first agent with tools: conversations about saved goals (consult, change, cancel, record progress).
        services.AddSingleton<GoalsAgent>();
        services.AddSingleton<PippinWorkflow>();
        services.AddSingleton<IPippin>(sp => sp.GetRequiredService<PippinWorkflow>());

        // Messages Pippin starts himself go out through a channel module (Communication registers Discord's);
        // without one, nothing is sent and nothing is marked as sent.
        services.TryAddSingleton<IPippinChannel, NoPippinChannel>();

        // Funcionalidade 2: the plan proposal, written and offered in the background by a Quartz job. The work to
        // do is in planning.proposals, so the job only polls; the host (the API) starts Quartz.
        services.AddSingleton<PlanReview>();
        services.AddSingleton<PlanProposer>();

        // Funcionalidade 3: the daily follow-up. The plan and the check-ins are in the database; the job looks every
        // minute for activities whose moment arrived (or still unanswered) and Pippin asks about them.
        services.AddSingleton<CheckInWriter>();

        // Funcionalidade 4: the monthly summary (and quarterly panorama), counted by Reporting and written by the AI.
        services.AddSingleton<SummaryWriter>();

        services.AddQuartz(quartz =>
        {
            quartz.AddJob<PlanProposalsJob>(job => job.WithIdentity(PlanProposalsJob.Key));
            quartz.AddTrigger(trigger => trigger
                .ForJob(PlanProposalsJob.Key)
                .WithIdentity($"{PlanProposalsJob.Key.Name}-trigger", PlanProposalsJob.Key.Group)
                .StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInSeconds(30).RepeatForever()));

            quartz.AddJob<DailyFollowUpJob>(job => job.WithIdentity(DailyFollowUpJob.Key));
            quartz.AddTrigger(trigger => trigger
                .ForJob(DailyFollowUpJob.Key)
                .WithIdentity($"{DailyFollowUpJob.Key.Name}-trigger", DailyFollowUpJob.Key.Group)
                .StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInSeconds(60).RepeatForever()));

            quartz.AddJob<MonthlySummaryJob>(job => job.WithIdentity(MonthlySummaryJob.Key));
            quartz.AddTrigger(trigger => trigger
                .ForJob(MonthlySummaryJob.Key)
                .WithIdentity($"{MonthlySummaryJob.Key.Name}-trigger", MonthlySummaryJob.Key.Group)
                .StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(5).RepeatForever()));
        });
        services.AddSingleton<IConversationReset, ConversationReset>();

        return services;
    }
}
