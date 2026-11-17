// LifeAim.Importer: brings the weight-loss journal a user kept by hand (a text file) into LifeAim.
//
//   dotnet run --project src/LifeAim.Importer -- --file <path> --user-id <LifeAim user id> [--only body|foods|meals]
//                                                [--year 2026] [--apply]
//
// Without --apply it is a simulation: it prints what would be saved and writes nothing. The file is personal data:
// it is read from wherever it is and never copied into the repository. The body section needs no AI; the nutrition
// table and the meals are transcribed by the AI (one call per day: it costs tokens), and a day far from the total the
// file itself states is reported instead of imported. Not part of the deploy: run it by hand, against the database
// in ConnectionStrings:LifeAim (User Secrets locally, the environment variable on a server).

using System.Globalization;
using LifeAim.BuildingBlocks.Database;
using LifeAim.Importer;
using LifeAim.Modules.Fitness;
using LifeAim.Modules.Fitness.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

string? Argument(string name) =>
    Array.IndexOf(args, name) is var index and >= 0 && index + 1 < args.Length ? args[index + 1] : null;

var file = Argument("--file");
var only = Argument("--only");
if (file is null || !Guid.TryParse(Argument("--user-id"), out var userId) || only is not (null or "body" or "foods" or "meals"))
{
    Console.Error.WriteLine("Uso: --file <arquivo> --user-id <id do usuário no LifeAim> [--only body|foods|meals] [--year AAAA] [--apply]");
    return 1;
}

if (!File.Exists(file))
{
    Console.Error.WriteLine($"Arquivo não encontrado: {file}");
    return 1;
}

var configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(Program).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddLifeAimDatabase(configuration);
services.AddFitnessModule();
await using var provider = services.BuildServiceProvider();

// The AI is only needed for the free-text sections; the body section imports without a key.
var key = configuration["OpenAI:ApiKey"];
if (only != "body" && string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("OpenAI:ApiKey não está configurada: só dá para importar a seção corporal (--only body).");
    return 1;
}

IChatClient chat = string.IsNullOrWhiteSpace(key)
    ? new NoChatClient()
    : new OpenAI.Chat.ChatClient(configuration["OpenAI:Model"] ?? "gpt-5-mini", key).AsIChatClient();

var year = int.TryParse(Argument("--year"), NumberStyles.None, CultureInfo.InvariantCulture, out var given) ? given : DateTime.Today.Year;
var importer = new HistoryImporter(provider.GetRequiredService<IFitnessJournal>(), new HistoryExtractor(chat), Console.Out);
await importer.RunAsync(
    await File.ReadAllTextAsync(file),
    new ImportOptions(userId, args.Contains("--apply"), only, year),
    CancellationToken.None);
return 0;

/// <summary>Stands in for the AI when only the body section is imported: it is never called.</summary>
internal sealed class NoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("OpenAI:ApiKey is not configured.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("OpenAI:ApiKey is not configured.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
