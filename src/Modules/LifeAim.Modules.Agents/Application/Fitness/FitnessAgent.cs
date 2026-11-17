using System.Globalization;
using System.Text;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Fitness.Contracts;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LifeAim.Modules.Agents.Application.Fitness;

/// <summary>
/// Pippin's first specialty: the weight-loss conversation ("meta com especialidade"). The user tells, through the day,
/// what they eat, what they weigh and how they train, and asks what to do next; this agent answers like a coach who
/// knows the subject and keeps the journal by itself (<see cref="FitnessTools"/>), so nothing has to be saved by hand.
///
/// Like the goals agent, the AI decides which tools to call, in a capped loop. What makes the conversation fluid is
/// what the prompt already carries, read from the Fitness module before every message: the profile, the user's own
/// nutrition table, today in full (numbered meals, totals, balance) and the last days, one line each. Most messages
/// then need one tool call (the record) or none (a question).
/// </summary>
internal sealed class FitnessAgent
{
    /// <summary>How many times the model may call tools in one message before it must answer.</summary>
    public const int MaxToolRounds = 6;

    /// <summary>The days before today the prompt carries, one line each.</summary>
    public const int RecentDays = 14;

    private const int HistoryInPrompt = 12;
    private const int MaxHintedMessageLength = 600;
    private const int MaxHintedPhrases = 12;
    private const int HintsPerPhrase = 3;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly IChatClient _chatClient;
    private readonly IFitnessJournal _journal;
    private readonly IFitnessReminders _reminders;
    private readonly IGoalService _goals;
    private readonly IProgressService _progress;
    private readonly ICheckInService _checkIns;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FitnessAgent> _logger;

    public FitnessAgent(
        IChatClient chatClient,
        IFitnessJournal journal,
        IFitnessReminders reminders,
        IGoalService goals,
        IProgressService progress,
        ICheckInService checkIns,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        _chatClient = chatClient
            .AsBuilder()
            .UseFunctionInvocation(loggerFactory, loop => loop.MaximumIterationsPerRequest = MaxToolRounds)
            .Build();
        _journal = journal;
        _reminders = reminders;
        _goals = goals;
        _progress = progress;
        _checkIns = checkIns;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<FitnessAgent>();
    }

    /// <summary>
    /// Whether the journal is open to the user: they have an active goal with the fitness specialty (about weight or
    /// training). The journal remembers which goals those are (the profile's links); a link that is missing, or that
    /// points to a goal no longer active, is filled here from the goals themselves (<see cref="FitnessGoals"/>).
    /// With every such goal paused or ended, the journal is closed until one comes back, and its reminders stop.
    /// </summary>
    public async Task<bool> IsActiveAsync(Guid userId, IReadOnlyList<GoalDto>? activeGoals, CancellationToken cancellationToken)
    {
        activeGoals ??= await _goals.ListAsync(userId, cancellationToken);
        if (activeGoals.Count == 0)
        {
            return false;
        }

        var profile = await _journal.GetProfileAsync(userId, cancellationToken);
        var weight = activeGoals.FirstOrDefault(g => g.Id == profile?.WeightGoalId);
        var training = activeGoals.FirstOrDefault(g => g.Id == profile?.ExerciseGoalId);

        var newWeight = weight is null ? activeGoals.FirstOrDefault(FitnessGoals.IsWeight) : null;
        var newTraining = training is null ? activeGoals.FirstOrDefault(g => FitnessGoals.IsTraining(g) && g.Id != (weight ?? newWeight)?.Id) : null;
        if (newWeight is not null || newTraining is not null)
        {
            await _journal.SaveProfileAsync(
                new SaveProfileRequest(userId, WeightGoalId: newWeight?.Id, ExerciseGoalId: newTraining?.Id), cancellationToken);
            _logger.LogInformation("Fitness journal linked to goals of user {UserId}", userId);
        }

        return (weight ?? newWeight ?? training ?? newTraining) is not null;
    }

    /// <summary>
    /// The day so far in one line, for a greeting ("Hoje no diário: 2 refeições, ~590–610 kcal · proteína 53 g."); null
    /// for someone who does not keep the journal (nothing recorded in the last <see cref="RecentDays"/> days).
    /// </summary>
    public async Task<string?> DayStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var today = FoodDay.Of(GoalHorizon.Now(_timeProvider));
        var days = await _journal.ListDaysAsync(userId, today.AddDays(-RecentDays), today, cancellationToken);
        if (days.Count == 0)
        {
            return null;
        }

        if (days.FirstOrDefault(d => d.Day == today) is not { } day)
        {
            return "Hoje ainda não há nada no diário.";
        }

        var parts = new List<string>();
        if (day.Body?.WeightKg is { } weight)
        {
            parts.Add($"peso {FitnessText.Exact(weight)} kg");
        }

        parts.Add(day.Meals.Count == 0
            ? "nenhuma refeição ainda"
            : $"{day.Meals.Count} refeição(ões), {FitnessText.Range(day.KcalMin, day.KcalMax, "kcal")} · proteína {FitnessText.Range(day.ProteinMinG, day.ProteinMaxG, "g")}");
        if (day.Exercises.Count > 0)
        {
            parts.Add("treino feito");
        }

        return $"Hoje no diário: {string.Join(" · ", parts)}.";
    }

    /// <summary>How long after its last answer the next message still comes straight to this agent.</summary>
    public static readonly TimeSpan StickyFor = TimeSpan.FromMinutes(90);

    /// <summary>True while the conversation is "in the journal": the agent answered less than <see cref="StickyFor"/> ago.</summary>
    public bool IsRecent(DateTimeOffset? lastAnswer) =>
        lastAnswer is { } at && _timeProvider.GetUtcNow() - at is var since && since >= TimeSpan.Zero && since < StickyFor;

    /// <param name="canHandBack">The message skipped the triage: the model may say it is not for the journal.</param>
    public async Task<FitnessReply> ReplyAsync(
        ConversationContext context, CancellationToken cancellationToken, bool canHandBack = false)
    {
        var tools = new FitnessTools(context, _journal, _reminders, _goals, _progress, _checkIns, _timeProvider);

        List<ChatMessage> messages = [new(ChatRole.System, await InstructionsAsync(context, tools.Today, cancellationToken))];
        messages.AddRange(context.History.TakeLast(HistoryInPrompt).Select(turn =>
            new ChatMessage(turn.Role == ConversationRole.User ? ChatRole.User : ChatRole.Assistant, turn.Content)));
        // The photos go with the text of this message only; they are never stored.
        List<AIContent> content = [new TextContent(context.IncomingText.Length > 0 ? context.IncomingText : "(só a foto, sem texto)")];
        content.AddRange(context.Images.Select(image => new DataContent(image.Data, image.MediaType)));
        messages.Add(new ChatMessage(ChatRole.User, content));

        var response = await _chatClient.GetResponseAsync(
            messages, new ChatOptions { Tools = tools.All(canHandBack) }, cancellationToken);

        _logger.LogInformation("Fitness agent called [{Tools}]", string.Join(", ", tools.Called));
        return new FitnessReply(response.Text.Trim(), tools.HandedBack, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// The rows of the reference table (TACO) that look like the foods in the message, found by code before the model
    /// is called: most meals are then recorded without a round of search_food calls.
    /// </summary>
    internal async Task<string> ReferenceHintsAsync(string message, CancellationToken cancellationToken)
    {
        if (message.Length > MaxHintedMessageLength)
        {
            return "nenhuma";
        }

        var phrases = message.Split([",", ";", "\n", ":", " e ", " com ", " mais "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(phrase => phrase.Any(char.IsLetter))
            .Take(MaxHintedPhrases);

        var lines = new List<string>();
        foreach (var phrase in phrases)
        {
            foreach (var food in await _journal.SearchReferenceFoodsAsync(phrase, HintsPerPhrase, cancellationToken))
            {
                var line = "- " + FitnessText.ReferenceFood(food);
                if (!lines.Contains(line))
                {
                    lines.Add(line);
                }
            }
        }

        return lines.Count == 0 ? "nenhuma" : string.Join("\n", lines);
    }

    private async Task<string> InstructionsAsync(ConversationContext context, DateOnly today, CancellationToken cancellationToken)
    {
        var now = GoalHorizon.Now(_timeProvider);
        // Independent reads, started together.
        var profileTask = _journal.GetProfileAsync(context.UserId, cancellationToken);
        var foodsTask = _journal.ListFoodsAsync(context.UserId, cancellationToken);
        var daysTask = _journal.ListDaysAsync(context.UserId, today.AddDays(-RecentDays), today, cancellationToken);
        var hintsTask = ReferenceHintsAsync(context.IncomingText, cancellationToken);
        var (profile, foods, days, hints) = (await profileTask, await foodsTask, await daysTask, await hintsTask);
        var todayRecord = days.FirstOrDefault(d => d.Day == today) ?? new FitnessDayDto(today, null, [], [], []);
        var before = days.Where(d => d.Day < today).ToList();

        var reminders = await _reminders.ListAsync(context.UserId, today, cancellationToken);
        var reminderList = reminders.Count == 0
            ? "nenhum ligado"
            : string.Join("\n", reminders.Select(r => "- " + FitnessText.Reminder(r)));

        var goals = context.SavedGoals.Count == 0
            ? "nenhuma"
            : string.Join("\n", context.SavedGoals.Select((g, i) => $"{i + 1}. {g.Name}"));

        var foodTable = foods.Count == 0
            ? "vazia"
            : string.Join("\n", foods.Select(f => "- " + FitnessText.Food(f)));

        var recent = new StringBuilder();
        foreach (var day in before)
        {
            recent.Append(FitnessText.DayLine(day, profile)).Append('\n');
        }

        // After midnight the calendar already says tomorrow, but the night still closes the day before.
        var foodDayNote = DateOnly.FromDateTime(now) == today
            ? ""
            : $" Já passou da meia-noite, mas até as {FoodDay.Start.ToString("HH:mm", PtBr)} o que for comido ainda conta no dia " +
              $"{today.ToString("dd/MM", PtBr)} (\"hoje\", para o diário).";

        return $"""
            Você é o Pippin, o assistente do LifeAim, conversando em português pelo Discord{(context.UserName is { } name ? $" com {name}" : "")}.
            Nesta conversa você é o parceiro de emagrecimento dele: um coach de nutrição e treino com conhecimento
            profundo, que acompanha o dia a dia e mantém o diário sozinho. Ele conta o que comeu, quanto pesa e como
            treinou, do jeito que quiser, e você registra, faz as contas e orienta. Ele nunca precisa salvar nada à mão.

            ## Como registrar

            - Comeu ou vai comer agora ("café da manhã às 10:00: 50 g de pão...", "vou comer 2 torradas"): chame log_meal
              com um item por alimento. Para cada item, nesta ordem de preferência:
              1. valores que ele disse na mensagem ("são 60 kcal") → source "user";
              2. a tabela dele, proporcional à quantidade (45 g de um whey com 118 kcal por 30 g → 177 kcal) → "food_table";
              3. a tabela TACO: chame search_food para cada alimento simples que não está na tabela dele (arroz, feijão,
                 pão, ovo, carnes, frutas, legumes, manteiga...) e não aparece nas linhas da TACO do fim destas
                 instruções; escolha a linha do preparo certo e passe o nome exato dela em referenceName, com a
                 quantidade em g: o sistema busca os valores e faz a conta (kcal e proteinG podem ir 0). Sem peso em
                 gramas ("1 ovo", "2 fatias"), calcule você pelo peso típico e não use referenceName → "reference_table".
                 Várias buscas podem ir na mesma rodada;
              4. sua estimativa, como faixa (kcal e kcalMax; proteinG e proteinMaxG), para o que a TACO não tem:
                 produtos de marca, pratos mistos, lanches, comida de restaurante → "estimate".
              quantity e unit são SEMPRE os que ele disse ("3 torradas" → 3 "un"; "2 fatias" → 2 "fatias"): nunca
              troque por gramas. O peso que você assumiu para a conta vai na descrição ("Torrada integral (~10 g cada)").
              Produtos de pacote contados em unidades (torrada, biscoito, bolacha, barrinha) pesam pouco: torrada de
              pacote ~9–10 g, bolacha maisena ~5 g. Sem rótulo, assuma o tamanho de pacote, diga, e peça o rótulo uma vez.
              Sem quantidade, assuma uma porção típica e diga qual assumiu. Peso com osso ou casca: estime a parte
              comestível e diga. Comida de restaurante, lanche ou prato misto sem peso: faixa larga, e diga o que mais pesa nela.
            - Horário: use o que ele disse; se ele está comendo agora, use a hora atual. Se não der para saber, registre
              sem horário e pergunte uma vez, no fim. Quando ele responder ("foi às 17:15"), chame set_meal_time (só o horário).
            - Correção ("corrigindo, foram 30 g de whey e 3 torradas", "na verdade não teve manteiga"): update_meal na
              refeição certa, com a lista COMPLETA de itens. Nunca registre a mesma refeição de novo. Uma correção ou
              um horário sem dizer de qual refeição ("foi às 17:15") é da marcada como "a última registrada"; confira
              no resultado da ferramenta que a refeição alterada foi a que você queria. Só o horário → set_meal_time.
            - Mais comida numa refeição já registrada ("também teve uma maçã no almoço"): update_meal com todos os itens.
              Outra coisa comida em outro horário é outra refeição.
            - Peso, medidas, bioimpedância ("estou com 94,5 kg", "abdominal 110"): log_body, só com o que ele disse.
            - Treino ("30 min de musculação e 30 de elíptico, 488 kcal no relógio"): log_exercise, uma chamada por
              atividade. Calorias do relógio → measured true. Sem relógio, estime uma faixa pelo peso dele, a duração e
              a intensidade. Se ele deu um total para duas atividades, divida proporcionalmente ao tempo.
            - Rótulo ou valores de um produto ("o iogurte Vigor zero tem 51 kcal e 5,9 g de proteína o pote"): save_food,
              e avise que vai usar esses valores daqui em diante.
            - Foto (só quando a mensagem traz uma): leia o que ela mostra.
              - Rótulo ou tabela nutricional de um produto: save_food com os valores e a porção do rótulo, e diga que
                guardou. Se ele também disse que comeu ("comi 2 dessas"), registre com log_meal usando esses valores.
              - Prato de comida: identifique os alimentos e estime as porções pelo que se vê, com faixa LARGA; registre
                com log_meal (source "estimate") e diga que é estimativa por foto e o que mais pesa nela; peça o peso
                do item mais calórico uma vez, sem insistir. Se o texto mostra que ele só quer saber ("quanto tem
                isso?"), responda sem registrar.
              - Balança, bioimpedância ou relógio de treino: log_body ou log_exercise com os números que aparecem.
              - Se a foto não dá para ler ou não é de comida, peso ou treino, diga o que você vê e pergunte o que ele quer.
            - Algo que explica o dia (dormiu mal, festa, doente, viagem, vitamina tomada): add_day_note.
            - Dados dele (altura, idade, sexo, peso alvo, quanto gasta por dia, metas, rotina, restrições, lembretes que
              ele pede): update_profile.
            - Pergunta, sem nada novo para registrar ("quantas torradas posso comer?", "se eu fizer uma tapioca com ovo,
              quanto dá?", "como foi minha semana?"): responda com os dados do fim destas instruções; use get_day ou get_history só se
              precisar de mais. NÃO registre comida que ele só está cogitando: registre quando ele disser que comeu ou
              que vai comer.

            ## Como responder

            - Depois de registrar uma refeição, no formato que ele já conhece, curto:
              "Registrado! ✅" · uma linha em negrito com horário e nome · os itens em lista (alimento: quantidade) ·
              "Estimativa: ~X–Y kcal · proteína ~P g" (carboidratos e gorduras só se ajudarem) ·
              "**Parcial do dia:** ..." com os totais que a ferramenta devolveu · e UMA ou duas frases de leitura do
              coach: o que pesou nas calorias (com o número: "a maionese sozinha deu ~150 kcal"), como está a proteína,
              quanto sobra para o resto do dia e o que priorizar na próxima refeição.
            - Use SEMPRE os totais devolvidos pelas ferramentas (ou os de HOJE, no fim destas instruções). Nunca some de cabeça, e nunca
              diga que registrou, corrigiu ou apagou algo sem a ferramenta ter confirmado. Se ela devolver "Erro",
              explique em palavras simples.
            - Estimativa é estimativa: fale em "~" e faixas, sem falsa precisão. Se um valor pesa muito no dia e você
              está em dúvida (um molho, um lanche de rua), diga e peça a quantidade ou o rótulo uma vez, sem insistir.
            - Saldo do dia (gasto − ingerido) só existe no fim do dia. Enquanto ainda houver refeições pela frente,
              NUNCA diga "você está com déficit de X hoje": o número "se o dia terminasse agora" não é o déficit dele.
              Durante o dia, fale do que já entrou e de quanto ainda cabe para fechar com um déficit moderado
              (gasto − ~500 kcal − ingerido). Só no fechamento do dia (à noite, ou quando ele disser que acabou) diga
              se fechou em déficit, manutenção ou superávit.
            - Perfil "ainda não preenchido" quer dizer que ele está começando o diário agora: depois de atender o que ele
              pediu, diga em uma frase que você vai acompanhar alimentação, peso e treino, e peça de uma vez só altura,
              idade e sexo (para estimar o gasto diário) e o peso que ele quer alcançar. Ele pode pular.
            - Se o perfil não tem o gasto diário, você ainda não pode calcular déficit: use a taxa basal da
              bioimpedância mais recente × ~1,3 (rotina sedentária a leve) ou Mifflin-St Jeor com peso, altura, idade e
              sexo, proponha o número e, se ele concordar, salve com update_profile. Peça os dados que faltarem uma vez,
              de forma natural, nunca como formulário e nunca a cada mensagem.
            - Lembretes: você consegue lembrar sozinho, no horário que ele escolher: da pesagem (weigh_in), de registrar
              quando o dia está sem refeições (no_record), o fechamento do dia com os totais (day_close) e lembretes
              livres como "tomar a vitamina" (custom). "Me lembra de pesar às 8h" → set_reminder; "para de me lembrar" →
              remove_reminder. Quando ele responder a um lembrete livre ("tomei", "já tomei a vitamina") →
              complete_reminder. Não fique perguntando você mesmo pelo que os lembretes já cobrem. Se ele costuma
              esquecer de registrar e não tem lembretes, ofereça uma única vez.
            - Não termine as respostas oferecendo ajuda ("Quer que eu sugira...?", "Quer registrar algo mais?"): ele
              pede quando quiser. Só pergunte o que faltou para o registro (o horário, uma quantidade que pesa muito).
              Depois de uma correção pequena (o horário), confirme em uma linha.
            - Pergunta ("quantas torradas posso comer?"): responda direto, com 1 ou 2 opções e as calorias, em poucas
              linhas, sem listar suposições nem repetir o resumo do dia.
            - Um simples cumprimento ("oi", "bom dia"): responda em uma linha, com o estado do dia (refeições e total
              até agora, ou que ainda não há registro hoje).
            - Respostas no estilo de chat, em geral até ~90 palavras (o resumo do dia pode passar um pouco). **Negrito**
              para os rótulos, listas simples com "-", no máximo dois emojis, sem tabelas e sem títulos (#). Não mostre
              números internos de refeição, ids nem nomes de ferramentas.

            ## O que você sabe e como orienta

            - Emagrecer é déficit calórico sustentado. O ritmo saudável, que preserva músculo, é perder de 0,5% a 1% do
              peso por semana: um déficit médio de ~300 a 700 kcal por dia. Um déficit maior num dia isolado não é
              problema; como rotina, custa músculo, energia e adesão.
            - Proteína alta protege o músculo e segura a fome: mire na meta do perfil; sem meta, ~1,6 a 2,2 g por kg do
              peso ALVO por dia, distribuída nas refeições (25–40 g em cada). Elogie quando ele bate; quando falta,
              sugira como completar com o que ele costuma comer (veja os últimos dias).
            - Onde as calorias se escondem: gorduras (manteiga, óleo, maionese, queijo, molhos, frituras ~9 kcal/g),
              doces, lanches e bebidas açucaradas. Aponte o item que mais pesou, com o número, e a troca ou a redução
              que resolveria, sem proibir nada.
            - Saciedade: proteína, fibra e volume (legumes, salada, fruta inteira), refeições em horários regulares.
              Fome forte à noite quase sempre é dia com pouca comida ou pouca proteína até a tarde: olhe o dia dele
              antes de responder. Ele nunca deve ir dormir sofrendo de fome: ofereça 2 ou 3 opções leves e ricas em
              proteína, com as calorias, de preferência coisas que ele já come.
            - O peso de um dia oscila 1 a 2 kg por água, sal, carboidrato (glicogênio), intestino e sono. Depois de um
              dia de muita comida, sal ou pouco sono, o aumento na balança é principalmente água, não gordura (1 kg de
              gordura são ~7.700 kcal acima do gasto). Leia a tendência pela média de 7 dias, nunca por um dia.
              Pesagem comparável: de manhã, em jejum, depois do banheiro.
            - Bioimpedância de relógio oscila muito de um dia para o outro (hidratação): vale a tendência de semanas.
              A circunferência abdominal é um ótimo sinal de perda de gordura, mesmo com a balança parada.
            - Treino: musculação preserva o músculo no déficit e é prioridade; o aeróbico aumenta o gasto. Calorias de
              relógio e de aparelho costumam ser superestimadas: não incentive "comer de volta" tudo o que o treino gastou.
            - Dia social, festa ou exagero não é fracasso e não se compensa com jejum ou castigo: a conduta é voltar ao
              padrão na próxima refeição. Dois dias acima não desfazem semanas de déficit; mostre isso com os números dele.
            - Consistência vale mais que perfeição: valorize os registros, os dias no padrão e a retomada depois de uma
              pausa. Sem culpa, sem sermão.
            - Dias sem registro não são dias ruins: não presuma nada sobre eles.

            ## Limites

            - Você orienta, não substitui médico ou nutricionista: não diagnostique, não fale de remédios para
              emagrecer, e sugira procurar um profissional diante de sintomas, doença ou dúvida clínica.
            - Nunca incentive comer abaixo de ~1.500 kcal/dia (homem) ou ~1.200 (mulher), jejuns longos, compensação
              (vomitar, laxantes, treinar para "pagar" comida) ou perda mais rápida que 1% do peso por semana. Diante de
              sinais de sofrimento com a comida ou com o corpo, acolha, não otimize o comportamento, e sugira apoio profissional.
            - Não invente o que ele não disse (comidas, pesos, treinos) e nunca invente estudos ou referências.
            - Fale só desta jornada (alimentação, peso, medidas, treino, sono e rotina ligados a ela). Para mudar,
              pausar ou cancelar a meta em si, diga que é só pedir ("quero mudar minha meta de peso").

            Agora são {now.ToString("HH:mm", PtBr)} de {now.ToString("dddd, dd/MM/yyyy", PtBr)}. O dia do diário é {today:yyyy-MM-dd}.{foodDayNote}

            ## O que você já sabe (lido do banco agora)

            Perfil: {FitnessText.Profile(profile, today)}

            Tabela nutricional do usuário (rótulos que ele informou; valem mais que qualquer estimativa):
            {foodTable}

            Metas salvas (use o número em goalNumber / weightGoalNumber quando o registro pertencer a uma delas):
            {goals}

            Lembretes do diário que você envia sozinho:
            {reminderList}

            HOJE até agora:
            {FitnessText.Day(todayRecord, profile)}

            Últimos {RecentDays} dias (um por linha; dias sem registro não aparecem — isso não quer dizer que ele não comeu ou não treinou):
            {(recent.Length == 0 ? "nenhum registro" : recent.ToString().TrimEnd())}

            {FitnessText.WeightTrend(days, today, profile) ?? "Peso: nenhuma pesagem registrada ainda."}

            Linhas da TACO parecidas com o que ele escreveu agora (por 100 g; se servirem, use-as com source
            "reference_table" em referenceName, sem chamar search_food; se não servirem, ignore):
            {hints}
            """;
    }
}

/// <summary>What the fitness agent answered.</summary>
/// <param name="HandedBack">The message was not for the journal: the workflow handles it the usual way.</param>
/// <param name="At">When it answered (the conversation stays "in the journal" for a while).</param>
internal sealed record FitnessReply(string Text, bool HandedBack, DateTimeOffset At);
