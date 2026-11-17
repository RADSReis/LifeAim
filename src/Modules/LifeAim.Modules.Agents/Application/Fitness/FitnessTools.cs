using System.ComponentModel;
using System.Globalization;
using System.Text;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Fitness.Contracts;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Fitness;

/// <summary>
/// The tools of the weight-loss conversation, created for ONE message of ONE user (like GoalTools): the model picks
/// the tool and fills the arguments; this code checks them and writes the journal through the Fitness module's
/// contract. Meals and exercises are referred to by their number in the day's list, never by id, and the user id
/// comes from the context. Every result is plain text and ends with the day as it is now, totals included, so the
/// model reports numbers the code added up. Problems come back as text too ("Erro: ...").
/// </summary>
internal sealed class FitnessTools(
    ConversationContext context,
    IFitnessJournal journal,
    IFitnessReminders reminders,
    IGoalService goals,
    IProgressService progress,
    ICheckInService checkIns,
    TimeProvider timeProvider)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>The longest period get_history answers at once.</summary>
    public const int MaxHistoryDays = 120;

    /// <summary>
    /// The model said the message is not for the journal (hand_back): the workflow handles it the usual way.
    /// </summary>
    public bool HandedBack { get; private set; }

    /// <summary>Names of the tools called, in order, for the log.</summary>
    public List<string> Called { get; } = [];

    /// <summary>The food day now: a ceia after midnight still belongs to the day before.</summary>
    public DateOnly Today => FoodDay.Of(GoalHorizon.Now(timeProvider));

    /// <param name="canHandBack">
    /// The message came straight here, without the usual triage, because the conversation was about the journal a
    /// moment ago: the model gets a way to say "this one is not mine".
    /// </param>
    public IList<AITool> All(bool canHandBack = false)
    {
        var tools = Journal();
        if (canHandBack)
        {
            tools.Add(AIFunctionFactory.Create(HandBack, "hand_back",
                "Use quando a mensagem NÃO é do diário de emagrecimento: cadastrar uma meta nova, ver, mudar, pausar ou " +
                "cancelar uma meta, o plano de uma meta, ou um assunto sem relação com alimentação, peso e treino. " +
                "Chame só esta ferramenta e não escreva resposta: outra parte do Pippin responde."));
        }

        return tools;
    }

    public string HandBack()
    {
        Called.Add("hand_back");
        HandedBack = true;
        return "Mensagem devolvida. Não escreva nada.";
    }

    private List<AITool> Journal() =>
    [
        AIFunctionFactory.Create(LogMealAsync, "log_meal",
            "Registra uma refeição ou lanche que o usuário comeu (ou disse que vai comer agora), com cada item e a " +
            "estimativa nutricional de cada um. Devolve o dia atualizado com os totais."),
        AIFunctionFactory.Create(UpdateMealAsync, "update_meal",
            "Corrige o que foi comido em uma refeição já registrada (e/ou o nome dela). Os items SUBSTITUEM todos os " +
            "itens da refeição: passe a lista completa, inclusive os que não mudaram. Para o horário, use set_meal_time."),
        AIFunctionFactory.Create(SetMealTimeAsync, "set_meal_time",
            "Informa ou corrige SÓ o horário de uma refeição já registrada (\"foi às 17:15\"). Não mexe nos itens."),
        AIFunctionFactory.Create(DeleteMealAsync, "delete_meal",
            "Apaga uma refeição registrada por engano (o usuário disse que não comeu, ou foi registrada em dobro)."),
        AIFunctionFactory.Create(LogBodyAsync, "log_body",
            "Registra o peso, as circunferências e/ou a bioimpedância de um dia. Informe só o que o usuário disse; o " +
            "registro do dia vai sendo completado, e um valor repetido substitui o anterior."),
        AIFunctionFactory.Create(LogExerciseAsync, "log_exercise",
            "Registra um treino ou exercício feito, com a duração e o gasto calórico (medido pelo relógio ou estimado). " +
            "Uma chamada por atividade (musculação e esteira são duas)."),
        AIFunctionFactory.Create(DeleteExerciseAsync, "delete_exercise", "Apaga um exercício registrado por engano."),
        AIFunctionFactory.Create(SaveFoodAsync, "save_food",
            "Guarda na tabela nutricional do usuário um alimento ou produto cujos valores ele informou (rótulo). " +
            "Um alimento com o mesmo nome é substituído."),
        AIFunctionFactory.Create(SearchFoodAsync, "search_food",
            "Procura um alimento na tabela de referência TACO (alimentos brasileiros comuns, valores por 100 g). Use " +
            "antes de estimar um alimento que não está na tabela do usuário. Uma chamada por alimento."),
        AIFunctionFactory.Create(UpdateProfileAsync, "update_profile",
            "Guarda dados do perfil do usuário para os cálculos (altura, ano de nascimento, sexo, peso alvo, gasto " +
            "diário, metas de calorias e proteína, observações). Informe só o que muda."),
        AIFunctionFactory.Create(AddDayNoteAsync, "add_day_note",
            "Anota algo que explica o dia: \"dormiu mal\", \"aniversário da esposa\", \"resfriado, sem treino\", " +
            "\"vitamina tomada\"."),
        AIFunctionFactory.Create(SetReminderAsync, "set_reminder",
            "Liga um lembrete do diário, ou muda o horário de um que já existe. O Pippin o envia sozinho, uma vez por dia."),
        AIFunctionFactory.Create(RemoveReminderAsync, "remove_reminder", "Desliga um lembrete do diário."),
        AIFunctionFactory.Create(CompleteReminderAsync, "complete_reminder",
            "Marca como feito hoje um lembrete livre (\"tomei a vitamina\"), para ele não ser enviado de novo hoje."),
        AIFunctionFactory.Create(GetDayAsync, "get_day",
            "Mostra tudo o que foi registrado em um dia (refeições com itens, corpo, exercícios, observações, totais). " +
            "O dia de hoje já está nas instruções; use para outros dias."),
        AIFunctionFactory.Create(GetHistoryAsync, "get_history",
            "Mostra um período do diário, uma linha por dia (peso, calorias, proteína, treino, saldo, observações), " +
            "com médias e a tendência do peso. Use para perguntas sobre períodos além dos últimos dias das instruções."),
    ];

    public async Task<string> LogMealAsync(
        [Description("Nome da refeição: \"Café da manhã\", \"Almoço\", \"Café da tarde\", \"Lanche\", \"Jantar\", \"Ceia\"...")]
        string name,
        [Description("Os itens da refeição, um por alimento.")] List<MealItemInput> items,
        [Description("Horário HH:mm (24h), se o usuário disse ou se está comendo agora; null se não der para saber.")]
        string? time = null,
        [Description("O dia, yyyy-MM-dd, só se NÃO for hoje (\"ontem jantei...\"); null para hoje.")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("log_meal");
        if (Day(date) is not { } day)
        {
            return BadDate(date);
        }

        if (!TryTime(time, out var eatenAt))
        {
            return BadTime(time);
        }

        try
        {
            var meal = await journal.AddMealAsync(
                new AddMealRequest(context.UserId, day, eatenAt, name, await ToItemsAsync(items, cancellationToken)), cancellationToken);
            return $"Refeição registrada: {meal.Name}" +
                   $" ({FitnessText.Range(meal.KcalMin, meal.KcalMax, "kcal")}, proteína {FitnessText.Range(meal.ProteinMinG, meal.ProteinMaxG, "g")})." +
                   (eatenAt is null ? " O horário não foi informado." : "") + "\n" + await DayTextAsync(day, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return $"Erro: {ex.Message} Nada foi registrado.";
        }
    }

    public async Task<string> UpdateMealAsync(
        [Description("O número da refeição na lista do dia.")] int mealNumber,
        [Description("Novo nome, ou null para manter.")] string? name = null,
        [Description("A lista COMPLETA de itens corrigida, ou null para manter os itens.")] List<MealItemInput>? items = null,
        [Description("O dia da refeição, yyyy-MM-dd; null para hoje.")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("update_meal");
        return await ChangeMealAsync(mealNumber, name, null, items, date, cancellationToken);
    }

    public async Task<string> SetMealTimeAsync(
        [Description("O número da refeição na lista do dia.")] int mealNumber,
        [Description("O horário HH:mm (24h).")] string time,
        [Description("O dia da refeição, yyyy-MM-dd; null para hoje.")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("set_meal_time");
        return TryTime(time, out var eatenAt) && eatenAt is not null
            ? await ChangeMealAsync(mealNumber, null, eatenAt, null, date, cancellationToken)
            : BadTime(time);
    }

    // The time and the items are changed by different tools on purpose: a model asked only for the time used to send
    // the items again from memory, and overwrote a correction made one message earlier.
    private async Task<string> ChangeMealAsync(
        int mealNumber, string? name, TimeOnly? eatenAt, List<MealItemInput>? items, string? date, CancellationToken cancellationToken)
    {
        if (Day(date) is not { } day)
        {
            return BadDate(date);
        }

        var meals = (await journal.GetDayAsync(context.UserId, day, cancellationToken)).Meals;
        if (mealNumber < 1 || mealNumber > meals.Count)
        {
            return $"Erro: não existe a refeição {mealNumber} em {day.ToString("dd/MM", PtBr)}. Nada foi alterado.\n" +
                   await DayTextAsync(day, cancellationToken);
        }

        try
        {
            var updated = await journal.UpdateMealAsync(
                new UpdateMealRequest(meals[mealNumber - 1].Id, context.UserId, name, eatenAt, items is null ? null : await ToItemsAsync(items, cancellationToken)),
                cancellationToken);
            return (updated is null ? "Erro: essa refeição não existe mais. Nada foi alterado." : $"Refeição corrigida: {updated.Name}.") +
                   "\n" + await DayTextAsync(day, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return $"Erro: {ex.Message} Nada foi alterado.";
        }
    }

    public async Task<string> DeleteMealAsync(
        [Description("O número da refeição na lista do dia.")] int mealNumber,
        [Description("O dia da refeição, yyyy-MM-dd; null para hoje.")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("delete_meal");
        if (Day(date) is not { } day)
        {
            return BadDate(date);
        }

        var meals = (await journal.GetDayAsync(context.UserId, day, cancellationToken)).Meals;
        if (mealNumber < 1 || mealNumber > meals.Count)
        {
            return $"Erro: não existe a refeição {mealNumber} em {day.ToString("dd/MM", PtBr)}. Nada foi apagado.";
        }

        var meal = meals[mealNumber - 1];
        await journal.DeleteMealAsync(meal.Id, context.UserId, cancellationToken);
        return $"Refeição apagada: {meal.Name}.\n" + await DayTextAsync(day, cancellationToken);
    }

    public async Task<string> LogBodyAsync(
        [Description("Peso em kg.")] decimal? weightKg = null,
        [Description("Circunferência abdominal (cintura) em cm.")] decimal? waistCm = null,
        [Description("Circunferência do quadril em cm.")] decimal? hipCm = null,
        [Description("Circunferência peitoral em cm.")] decimal? chestCm = null,
        [Description("Músculo esquelético em kg (bioimpedância).")] decimal? muscleKg = null,
        [Description("Massa gorda em kg (bioimpedância).")] decimal? fatKg = null,
        [Description("Água corporal em kg (bioimpedância).")] decimal? waterKg = null,
        [Description("Gordura corporal em % (bioimpedância).")] decimal? fatPercent = null,
        [Description("IMC.")] decimal? bmi = null,
        [Description("Taxa de metabolismo basal em kcal (bioimpedância).")] int? bmrKcal = null,
        [Description("Observação sobre a medição, nas palavras do usuário (\"depois de 2 dias comendo muito\"); senão null.")]
        string? note = null,
        [Description("O dia da medição, yyyy-MM-dd; null para hoje.")] string? date = null,
        [Description("O número, na lista de metas salvas, da meta de peso/emagrecimento, se houver uma; senão null.")]
        int? weightGoalNumber = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("log_body");
        if (Day(date) is not { } day)
        {
            return BadDate(date);
        }

        try
        {
            var record = await journal.RecordBodyAsync(
                new RecordBodyRequest(
                    context.UserId, day, weightKg, waistCm, hipCm, chestCm, muscleKg, fatKg, waterKg, fatPercent, bmi, bmrKcal, note),
                cancellationToken);

            // The weight also counts as progress of the weight goal, so the follow-up, the summaries and the
            // priorities review (Funcionalidades 3 to 5) see it like any other report.
            var linked = "";
            if (weightKg is { } kg && await LinkedGoalAsync(weightGoalNumber, weight: true, cancellationToken) is { } goal)
            {
                var entry = await progress.RecordAsync(
                    new RecordProgressRequest(context.UserId, goal.Id, day, $"pesou {FitnessText.Exact(kg)} kg", kg, "kg"),
                    cancellationToken);
                await checkIns.MarkDoneForGoalAsync(context.UserId, goal.Id, day, entry.Id, cancellationToken);
                linked = $" Também contou como progresso da meta \"{goal.Name}\".";
            }

            var history = await journal.ListDaysAsync(context.UserId, day.AddDays(-30), day, cancellationToken);
            var profile = await journal.GetProfileAsync(context.UserId, cancellationToken);
            return $"Registrado em {day.ToString("dd/MM/yyyy", PtBr)}. {FitnessText.Body(record)}.{linked}\n" +
                   FitnessText.WeightTrend(history, day, profile);
        }
        catch (ArgumentException ex)
        {
            return $"Erro: {ex.Message} Nada foi registrado.";
        }
    }

    public async Task<string> LogExerciseAsync(
        [Description("O que foi feito: \"Musculação (perna)\", \"Esteira 5 km/h com 9% de inclinação\", \"Elíptico\".")]
        string activity,
        [Description("Duração em minutos, se o usuário disse; senão null.")] int? durationMinutes = null,
        [Description("Gasto em kcal: o valor do relógio, ou o mínimo da sua estimativa; null se não der para estimar.")]
        decimal? kcal = null,
        [Description("O máximo da estimativa em kcal; null quando o valor é exato (relógio).")] decimal? kcalMax = null,
        [Description("true se as calorias vieram de um aparelho (smartwatch); false se é estimativa.")] bool measured = false,
        [Description("Horário HH:mm (24h) em que treinou, se o usuário disse; senão null.")] string? time = null,
        [Description("O dia, yyyy-MM-dd; null para hoje.")] string? date = null,
        [Description("O número, na lista de metas salvas, da meta a que esse treino pertence (academia, corrida...), se houver; senão null.")]
        int? goalNumber = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("log_exercise");
        if (Day(date) is not { } day)
        {
            return BadDate(date);
        }

        if (!TryTime(time, out var doneAt))
        {
            return BadTime(time);
        }

        try
        {
            var exercise = await journal.AddExerciseAsync(
                new AddExerciseRequest(context.UserId, day, doneAt, activity, durationMinutes, kcal, kcalMax, measured && kcal is not null),
                cancellationToken);

            // A workout that belongs to a saved goal is progress of that goal too, and answers its follow-up of the day.
            var linked = "";
            if (await LinkedGoalAsync(goalNumber, weight: false, cancellationToken) is { } goal)
            {
                var entry = await progress.RecordAsync(
                    new RecordProgressRequest(
                        context.UserId, goal.Id, day, exercise.Activity,
                        durationMinutes is > 0 ? durationMinutes : null, durationMinutes is > 0 ? "min" : null),
                    cancellationToken);
                await checkIns.MarkDoneForGoalAsync(context.UserId, goal.Id, day, entry.Id, cancellationToken);
                linked = $" Também contou como progresso da meta \"{goal.Name}\".";
            }

            return $"Exercício registrado: {FitnessText.Exercise(exercise)}.{linked}\n" + await DayTextAsync(day, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return $"Erro: {ex.Message} Nada foi registrado.";
        }
    }

    public async Task<string> DeleteExerciseAsync(
        [Description("O número do exercício na lista do dia.")] int exerciseNumber,
        [Description("O dia, yyyy-MM-dd; null para hoje.")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("delete_exercise");
        if (Day(date) is not { } day)
        {
            return BadDate(date);
        }

        var exercises = (await journal.GetDayAsync(context.UserId, day, cancellationToken)).Exercises;
        if (exerciseNumber < 1 || exerciseNumber > exercises.Count)
        {
            return $"Erro: não existe o exercício {exerciseNumber} em {day.ToString("dd/MM", PtBr)}. Nada foi apagado.";
        }

        await journal.DeleteExerciseAsync(exercises[exerciseNumber - 1].Id, context.UserId, cancellationToken);
        return $"Exercício apagado: {exercises[exerciseNumber - 1].Activity}.\n" + await DayTextAsync(day, cancellationToken);
    }

    public async Task<string> SaveFoodAsync(
        [Description("Nome do alimento ou produto, com marca e sabor se houver (\"Iogurte Grego Vigor Zero Morango\").")]
        string name,
        [Description("A porção a que os valores se referem, como no rótulo: \"30 g (1 scoop)\", \"1 pote de 90 g\", \"100 g\".")]
        string serving,
        [Description("Calorias (kcal) da porção.")] decimal kcal,
        [Description("Proteínas (g) da porção, se informadas.")] decimal? proteinG = null,
        [Description("Carboidratos (g) da porção, se informados.")] decimal? carbsG = null,
        [Description("Gorduras totais (g) da porção, se informadas.")] decimal? fatG = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("save_food");
        try
        {
            var food = await journal.SaveFoodAsync(
                new SaveFoodRequest(context.UserId, name, serving, kcal, proteinG, carbsG, fatG), cancellationToken);
            return $"Guardado na tabela do usuário: {FitnessText.Food(food)}. Vale para as próximas vezes.";
        }
        catch (ArgumentException ex)
        {
            return $"Erro: {ex.Message} Nada foi guardado.";
        }
    }

    public async Task<string> SearchFoodAsync(
        [Description("O alimento e o preparo, em poucas palavras: \"arroz cozido\", \"pão francês\", \"frango peito grelhado\", \"ovo frito\".")]
        string query,
        CancellationToken cancellationToken = default)
    {
        Called.Add("search_food");
        var foods = await journal.SearchReferenceFoodsAsync(query, 8, cancellationToken);
        return foods.Count == 0
            ? $"Nada parecido com \"{query}\" na TACO. Estime você (source \"estimate\")."
            : "TACO (valores por 100 g; calcule proporcional à quantidade e use source \"reference_table\"). Escolha a linha do " +
              "preparo que o usuário comeu (cozido/grelhado/frito), não a crua, salvo se ele pesou cru:\n" +
              string.Join("\n", foods.Select(f => "- " + FitnessText.ReferenceFood(f)));
    }

    public async Task<string> UpdateProfileAsync(
        [Description("Altura em cm (1,76 m → 176).")] decimal? heightCm = null,
        [Description("Ano de nascimento (\"tenho 34 anos\" → calcule a partir de hoje).")] int? birthYear = null,
        [Description("\"male\" ou \"female\".")] string? sex = null,
        [Description("Peso que o usuário quer alcançar, em kg.")] decimal? targetWeightKg = null,
        [Description("Gasto diário estimado SEM os treinos (metabolismo + atividades normais), em kcal.")] int? dailyEnergyKcal = null,
        [Description("Quanto comer por dia, em kcal, se o usuário (ou um nutricionista) definiu um número.")] int? calorieTargetKcal = null,
        [Description("Meta diária de proteína em g.")] int? proteinTargetG = null,
        [Description("Rotina, restrições, preferências e lembretes pedidos, em texto. SUBSTITUI as observações atuais: inclua o que já havia.")]
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("update_profile");
        Sex? parsedSex = sex?.Trim().ToLowerInvariant() switch
        {
            "male" => Sex.Male,
            "female" => Sex.Female,
            _ => null,
        };
        if (sex is not null && parsedSex is null)
        {
            return "Erro: sex precisa ser \"male\" ou \"female\". Nada foi alterado.";
        }

        try
        {
            var profile = await journal.SaveProfileAsync(
                new SaveProfileRequest(
                    context.UserId, heightCm, birthYear, parsedSex, targetWeightKg, dailyEnergyKcal, calorieTargetKcal, proteinTargetG, notes),
                cancellationToken);
            return "Perfil salvo: " + FitnessText.Profile(profile, Today) + ".";
        }
        catch (ArgumentException ex)
        {
            return $"Erro: {ex.Message} Nada foi alterado.";
        }
    }

    public async Task<string> AddDayNoteAsync(
        [Description("A observação, curta, na terceira pessoa (\"dormiu mal: filho doente\").")] string note,
        [Description("O dia, yyyy-MM-dd; null para hoje.")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("add_day_note");
        if (Day(date) is not { } day)
        {
            return BadDate(date);
        }

        try
        {
            await journal.AddDayNoteAsync(context.UserId, day, note, cancellationToken);
            return $"Observação anotada em {day.ToString("dd/MM/yyyy", PtBr)}.";
        }
        catch (ArgumentException ex)
        {
            return $"Erro: {ex.Message} Nada foi anotado.";
        }
    }

    public async Task<string> SetReminderAsync(
        [Description("\"weigh_in\" (pesagem, se ainda não houver peso no dia), \"no_record\" (aviso se o dia estiver sem refeição " +
                     "registrada), \"day_close\" (fechamento do dia com os totais) ou \"custom\" (lembrete livre).")]
        string kind,
        [Description("Horário HH:mm (24h) que o usuário escolheu; null para o padrão (pesagem 09:00, sem registro 14:00, fechamento 21:30).")]
        string? time = null,
        [Description("Só para \"custom\": do que é o lembrete, no infinitivo (\"tomar a vitamina\").")] string? text = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("set_reminder");
        if (ParseKind(kind) is not { } parsed)
        {
            return BadKind(kind);
        }

        if (!TryTime(time, out var at))
        {
            return BadTime(time);
        }

        if (parsed == ReminderKind.Custom && at is null)
        {
            return "Erro: um lembrete livre precisa do horário. Pergunte ao usuário a que horas. Nada foi ligado.";
        }

        try
        {
            var reminder = await reminders.SetAsync(
                new SetReminderRequest(context.UserId, parsed, at ?? FitnessReminderPlanner.DefaultTime(parsed), text), cancellationToken);
            return $"Lembrete ligado: {FitnessText.Reminder(reminder)}. O Pippin envia sozinho, uma vez por dia, só se ainda for preciso.";
        }
        catch (ArgumentException ex)
        {
            return $"Erro: {ex.Message} Nada foi ligado.";
        }
    }

    public async Task<string> RemoveReminderAsync(
        [Description("\"weigh_in\", \"no_record\", \"day_close\" ou \"custom\".")] string kind,
        [Description("Só para \"custom\": o texto do lembrete, como aparece na lista.")] string? text = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("remove_reminder");
        if (ParseKind(kind) is not { } parsed)
        {
            return BadKind(kind);
        }

        return await reminders.RemoveAsync(context.UserId, parsed, text, cancellationToken)
            ? "Lembrete desligado."
            : "Erro: esse lembrete não está ligado. Confira a lista de lembretes.";
    }

    public async Task<string> CompleteReminderAsync(
        [Description("O texto do lembrete livre, como aparece na lista de lembretes.")] string text,
        CancellationToken cancellationToken = default)
    {
        Called.Add("complete_reminder");
        var today = Today;
        var reminder = (await reminders.ListAsync(context.UserId, today, cancellationToken))
            .FirstOrDefault(r => r.Kind == ReminderKind.Custom && string.Equals(r.Text, text?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (reminder is null)
        {
            return "Erro: não existe um lembrete livre com esse texto. Confira a lista de lembretes.";
        }

        await reminders.MarkDoneAsync(reminder.Id, context.UserId, today, cancellationToken);
        return $"Feito hoje: {reminder.Text}. Não será lembrado de novo hoje.";
    }

    private static ReminderKind? ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "weigh_in" => ReminderKind.WeighIn,
        "no_record" => ReminderKind.NoRecord,
        "day_close" => ReminderKind.DayClose,
        "custom" => ReminderKind.Custom,
        _ => null,
    };

    private static string BadKind(string? kind) =>
        $"Erro: \"{kind}\" não é um tipo de lembrete (weigh_in, no_record, day_close ou custom). Nada foi feito.";

    public async Task<string> GetDayAsync(
        [Description("O dia, yyyy-MM-dd.")] string date,
        CancellationToken cancellationToken = default)
    {
        Called.Add("get_day");
        return Day(date) is { } day ? await DayTextAsync(day, cancellationToken) : BadDate(date);
    }

    public async Task<string> GetHistoryAsync(
        [Description("Primeiro dia do período, yyyy-MM-dd.")] string from,
        [Description("Último dia do período, yyyy-MM-dd; null para hoje.")] string? to = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("get_history");
        if (GoalExtraction.ParseDate(from) is not { } first)
        {
            return BadDate(from);
        }

        if (Day(to) is not { } last)
        {
            return BadDate(to);
        }

        if (last < first)
        {
            return "Erro: o período termina antes de começar.";
        }

        var cut = last.DayNumber - first.DayNumber >= MaxHistoryDays;
        if (cut)
        {
            first = last.AddDays(-(MaxHistoryDays - 1));
        }

        var days = await journal.ListDaysAsync(context.UserId, first, last, cancellationToken);
        var profile = await journal.GetProfileAsync(context.UserId, cancellationToken);
        return (cut ? $"(Período longo demais: mostrando só os últimos {MaxHistoryDays} dias.)\n" : "") + History(days, first, last, profile);
    }

    /// <summary>A period as text: one line per day, the averages of the days with food recorded, and the weight trend.</summary>
    public static string History(IReadOnlyList<FitnessDayDto> days, DateOnly from, DateOnly to, FitnessProfileDto? profile)
    {
        var period = $"{from.ToString("dd/MM/yyyy", PtBr)} a {to.ToString("dd/MM/yyyy", PtBr)}";
        if (days.Count == 0)
        {
            return $"Nada registrado de {period}.";
        }

        var text = new StringBuilder($"Diário de {period} ({days.Count} dia(s) com registro):");
        foreach (var day in days)
        {
            text.Append('\n').Append(FitnessText.DayLine(day, profile));
        }

        var fed = days.Where(d => d.Meals.Count > 0).ToList();
        if (fed.Count > 1)
        {
            text.Append($"\nMédia dos {fed.Count} dias com refeições registradas: ingerido ")
                .Append(FitnessText.Range(fed.Average(d => d.KcalMin), fed.Average(d => d.KcalMax), "kcal"))
                .Append(" · proteína ")
                .Append(FitnessText.Range(fed.Average(d => d.ProteinMinG), fed.Average(d => d.ProteinMaxG), "g"));
        }

        var trained = days.Count(d => d.Exercises.Count > 0);
        text.Append($"\nDias com treino: {trained}.");
        if (FitnessText.WeightTrend(days, to, profile) is { } trend)
        {
            text.Append('\n').Append(trend);
        }

        return text.ToString();
    }

    private async Task<string> DayTextAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var record = await journal.GetDayAsync(context.UserId, day, cancellationToken);
        var profile = await journal.GetProfileAsync(context.UserId, cancellationToken);
        return FitnessText.Day(record, profile);
    }

    /// <summary>
    /// The items as the journal keeps them. An item that names a row of the reference table (TACO) with an amount in
    /// grams gets its numbers from the table, calculated here: the model neither copies the values nor multiplies.
    /// </summary>
    private async Task<IReadOnlyList<MealItemDto>> ToItemsAsync(List<MealItemInput>? items, CancellationToken cancellationToken)
    {
        var result = new List<MealItemInput>();
        foreach (var item in items ?? [])
        {
            var isWeight = item.Quantity is > 0 && item.Unit?.Trim().ToLowerInvariant() is "g" or "ml";
            if (!string.IsNullOrWhiteSpace(item.ReferenceName) && isWeight
                && await journal.FindReferenceFoodAsync(item.ReferenceName, cancellationToken) is { } food)
            {
                var factor = item.Quantity!.Value / 100m;
                result.Add(item with
                {
                    Description = string.IsNullOrWhiteSpace(item.Description) ? food.Name : item.Description,
                    Kcal = food.KcalPer100g * factor,
                    KcalMax = null,
                    ProteinG = (food.ProteinG ?? 0) * factor,
                    ProteinMaxG = null,
                    CarbsG = food.CarbsG * factor,
                    FatG = food.FatG * factor,
                    Source = "reference_table",
                });
                continue;
            }

            // Not a row of the table after all: the numbers are the model's, so they are an estimate.
            result.Add(item.Source?.Trim().ToLowerInvariant() == "reference_table" && string.IsNullOrWhiteSpace(item.ReferenceName)
                ? item with { Source = "estimate" }
                : item);
        }

        return ToItems(result);
    }

    private static IReadOnlyList<MealItemDto> ToItems(List<MealItemInput> items) =>
        items.Select(i => new MealItemDto(
            i.Description ?? "",
            i.Quantity is > 0 ? i.Quantity : null,
            i.Unit,
            Math.Round(i.Kcal, 1),
            Math.Round(i.KcalMax ?? i.Kcal, 1),
            Math.Round(i.ProteinG, 1),
            Math.Round(i.ProteinMaxG ?? i.ProteinG, 1),
            i.CarbsG is { } carbs ? Math.Round(carbs, 1) : null,
            i.FatG is { } fat ? Math.Round(fat, 1) : null,
            i.Source?.Trim().ToLowerInvariant() switch
            {
                "user" => NutritionSource.User,
                "food_table" => NutritionSource.FoodTable,
                "reference_table" => NutritionSource.ReferenceTable,
                _ => NutritionSource.Estimate,
            })).ToList();

    /// <summary>
    /// The goal a weigh-in (or a workout) is progress of: the one the model pointed at, remembered in the profile for
    /// the next times; without a number, the one remembered, while it is still an active goal.
    /// </summary>
    private async Task<GoalDto?> LinkedGoalAsync(int? goalNumber, bool weight, CancellationToken cancellationToken)
    {
        var profile = await journal.GetProfileAsync(context.UserId, cancellationToken);
        var remembered = weight ? profile?.WeightGoalId : profile?.ExerciseGoalId;

        if (await GoalAsync(goalNumber, cancellationToken) is { } chosen)
        {
            if (chosen.Id != remembered)
            {
                await journal.SaveProfileAsync(
                    weight
                        ? new SaveProfileRequest(context.UserId, WeightGoalId: chosen.Id)
                        : new SaveProfileRequest(context.UserId, ExerciseGoalId: chosen.Id),
                    cancellationToken);
            }

            return chosen;
        }

        return remembered is { } id
            ? (await goals.ListAsync(context.UserId, cancellationToken)).FirstOrDefault(g => g.Id == id)
            : null;
    }

    // The numbers are the ones in the prompt's list of saved goals (active, oldest first), read again like GoalTools.
    private async Task<GoalDto?> GoalAsync(int? goalNumber, CancellationToken cancellationToken)
    {
        if (goalNumber is not { } n)
        {
            return null;
        }

        var saved = await goals.ListAsync(context.UserId, cancellationToken);
        return n >= 1 && n <= saved.Count ? saved[n - 1] : null;
    }

    /// <summary>The day a tool call is about: today (the food day) when null, never the future.</summary>
    private DateOnly? Day(string? date)
    {
        if (date is null)
        {
            return Today;
        }

        // One day of slack: "hoje" by the calendar after midnight is tomorrow by the food day.
        return GoalExtraction.ParseDate(date) is { } day && day <= Today.AddDays(1) ? day : null;
    }

    private static bool TryTime(string? time, out TimeOnly? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(time))
        {
            return true;
        }

        if (TimeOnly.TryParseExact(time.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            parsed = new TimeOnly(value.Hour, value.Minute);
            return true;
        }

        return false;
    }

    private static string BadDate(string? date) =>
        $"Erro: \"{date}\" não é um dia válido (formato yyyy-MM-dd, e não pode ser no futuro). Nada foi feito.";

    private static string BadTime(string? time) => $"Erro: \"{time}\" não é um horário no formato HH:mm. Nada foi feito.";
}

/// <summary>
/// One item of a meal as the model fills it. Calories and protein may be a range (kcal..kcalMax) when they are an
/// estimate; an exact value leaves the "max" null.
/// </summary>
internal sealed record MealItemInput(
    [property: Description("O alimento: \"Pão francês\", \"Frango cozido (parte comestível de 250 g com osso)\".")]
    string? Description,
    [property: Description("A quantidade que o usuário disse (50); null se ele não disse.")] decimal? Quantity,
    [property: Description("A unidade da quantidade: \"g\", \"ml\", \"un\", \"fatias\", \"scoop\"; null sem quantidade.")] string? Unit,
    [property: Description("Calorias (kcal) DESTA quantidade (com referenceName, pode ser 0): o valor exato, ou o mínimo da estimativa.")] decimal Kcal,
    [property: Description("O máximo da estimativa de kcal; null quando o valor é exato.")] decimal? KcalMax,
    [property: Description("Proteína (g) DESTA quantidade (com referenceName, pode ser 0): o valor exato, ou o mínimo da estimativa.")] decimal ProteinG,
    [property: Description("O máximo da estimativa de proteína; null quando o valor é exato.")] decimal? ProteinMaxG,
    [property: Description("Carboidratos (g) desta quantidade, se der para estimar; senão null.")] decimal? CarbsG,
    [property: Description("Gorduras (g) desta quantidade, se der para estimar; senão null.")] decimal? FatG,
    [property: Description("De onde vieram os valores: \"user\" (o usuário disse), \"food_table\" (tabela do usuário), \"reference_table\" (TACO, via search_food) ou \"estimate\" (sua estimativa).")]
    string? Source,
    [property: Description("O nome EXATO de uma linha da TACO (\"Arroz, tipo 1, cozido\"), quando o item é esse alimento e quantity está em g: o sistema busca os valores e calcula o proporcional, ignorando kcal e proteinG. null para todo o resto.")]
    string? ReferenceName = null);
