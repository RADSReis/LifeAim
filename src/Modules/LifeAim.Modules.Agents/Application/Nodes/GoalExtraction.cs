using System.Globalization;
using System.Text;
using LifeAim.Modules.Agents.Application.Workflow;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// One goal as the AI returns it (structured output), shared by the nodes that understand goals.
/// Dates are text ("2026-12-25") because a date format is easier for the model to follow than a type,
/// and the code checks it; numbers are numbers.
/// </summary>
internal sealed record ExtractedGoal(
    string Name,
    string? Description,
    string? Indicator,
    decimal? StartValue,
    decimal? TargetValue,
    string? Unit,
    string? DueDate);

/// <summary>A doubt as the AI returns it: the question and its options (without letters; the code adds them).</summary>
internal sealed record ExtractedDoubt(string Question, List<string>? Options);

/// <summary>What the goal nodes share: the AI's rules for one goal, the conversion to drafts and the summary.</summary>
internal static class GoalExtraction
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>How each field of a goal must be filled. Part of both nodes' prompts.</summary>
    public const string GoalFieldRules =
        """
        Cada meta tem:
        - name: curto e claro, começando com um verbo no infinitivo ("Emagrecer até 90 kg", "Ler 2 livros").
        - description: contexto útil que o usuário deu e que não está nos outros campos (ex.: "está terminando
          um livro"), ou null. Não repita números que já estão em startValue ou targetValue.
        - indicator: o que se mede, em palavras ("peso corporal", "livros terminados"), ou null se não é mensurável.
        - startValue: o valor de HOJE do próprio indicador, só se o usuário disse esse valor
          ("estou com 94 kg" → 94 para "peso corporal"); senão null.
          Metas de quantidade contam o que será feito dentro do período ("ler 2 livros", "correr 10 vezes",
          "juntar R$ 1000"): a contagem começa do zero, então startValue é null.
          Algo em andamento ("estou no fim de 1 livro") NÃO é valor inicial: vai para a description e,
          se mudar a conta, vira uma dúvida.
        - targetValue: o valor que ele quer atingir ("chegar a 90 kg" → 90; "ler mais 2 livros" → 2), senão null.
        - unit: a unidade dos valores ("kg", "livros", "km"), ou null.
        - dueDate: o prazo desta meta no formato yyyy-MM-dd. Se a meta tem prazo próprio, use-o ("até o natal" → 25/12);
          senão use o fim do período escolhido; null se nada foi dito.
        Nunca invente metas, números ou datas que o usuário não disse.
        """;

    /// <summary>How the AI must write doubts: always a choice, never a yes/no question. Part of both prompts.</summary>
    public const string DoubtRules =
        """
        Dúvidas (doubts): só quando algo ambíguo muda o número, a data ou o que se mede. No máximo 2.
        Cada dúvida é uma ESCOLHA entre opções, para o usuário responder só com uma letra:
        - question: pergunta curta que NÃO se responde com "sim" ou "não".
          Proibido: "Os 2 livros são além do que você está terminando?". Certo: "Quantos livros a meta vai contar?".
        - options: 2 ou 3 opções que se excluem. Cada opção é uma frase curta e completa que, sozinha, define a
          meta sem margem de interpretação, começando pelo valor final quando houver número.
          Ex.: ["3 livros: o que estou terminando + 2 novos", "2 livros: só os novos, sem contar o que estou terminando"].
          Proibido: opções vagas ("outro", "depende", "não sei", "mais ou menos") ou que se sobreponham.
          Não escreva letras nas opções; o Pippin põe as letras.
        - Nunca pergunte o que o usuário já disse com clareza. Lista vazia se tudo está claro.
        Enquanto houver uma dúvida, preencha a meta com a opção mais provável; ela será corrigida pela escolha.
        """;

    /// <summary>"A", "B", "C"… continuing across doubts, so every letter in a message means one thing only.</summary>
    public static IEnumerable<(string Letter, string Option)> Lettered(GoalDoubt doubt, int firstIndex) =>
        doubt.Options.Select((option, i) => (((char)('A' + firstIndex + i)).ToString(), option));

    /// <summary>Keeps only well-formed choices: a question and 2 or 3 distinct options. At most 2 doubts.</summary>
    public static IReadOnlyList<GoalDoubt> ToDoubts(IEnumerable<ExtractedDoubt>? doubts) =>
        (doubts ?? [])
            .Select(d => new GoalDoubt(
                d.Question?.Trim() ?? "",
                (d.Options ?? []).Select(o => o?.Trim() ?? "").Where(o => o.Length > 0).Distinct().Take(3).ToList()))
            .Where(d => d.Question.Length > 0 && d.Options.Count >= 2)
            .Take(2)
            .ToList();

    public static IReadOnlyList<GoalDraft> ToDrafts(IEnumerable<ExtractedGoal>? goals, DateOnly? horizon) =>
        (goals ?? [])
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .Select(g => new GoalDraft(
                g.Name.Trim(),
                Blank(g.Description),
                Blank(g.Indicator),
                g.StartValue,
                g.TargetValue,
                Blank(g.Unit),
                ParseDate(g.DueDate) ?? horizon))
            .ToList();

    /// <summary>"2026-12-25" → a date; anything else (or a mistake of the model) → null.</summary>
    public static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>The numbered list Pippin shows: "1. Emagrecer até 90 kg — peso corporal: de 94 para 90 kg · até 25/12/2026".</summary>
    public static string List(IEnumerable<GoalDraft> goals)
    {
        var text = new StringBuilder();
        var number = 1;
        foreach (var goal in goals)
        {
            var details = new List<string>();
            var values = Values(goal);
            if (goal.Indicator is not null || values is not null)
            {
                details.Add(string.Join(": ", new[] { goal.Indicator, values }.Where(part => part is not null)));
            }

            if (goal.DueDate is { } due)
            {
                details.Add("até " + due.ToString("dd/MM/yyyy", PtBr));
            }

            text.Append(number++).Append(". **").Append(goal.Name).Append("**");
            if (details.Count > 0)
            {
                text.Append(" — ").Append(string.Join(" · ", details));
            }

            text.Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The confirmation question. With open doubts it asks only for the choice (a letter), since a "sim"
    /// there would answer two questions at once; without doubts it asks "sim" to save.
    /// </summary>
    public static string Confirmation(GoalsDraft draft)
    {
        var count = draft.Goals.Count;
        var text = new StringBuilder()
            .Append(count == 1 ? "Entendi esta meta" : $"Entendi estas {count} metas")
            .Append(":\n")
            .Append(List(draft.Goals));

        if (draft.Doubts.Count == 0)
        {
            text.Append("\n\nEstá certo? Responda **sim** para salvar, ou me diga o que quer mudar.");
            return text.ToString();
        }

        text.Append("\n\nAntes de salvar, preciso que você escolha:");
        var index = 0;
        foreach (var doubt in draft.Doubts)
        {
            text.Append("\n\n❓ ").Append(doubt.Question);
            foreach (var (letter, option) in Lettered(doubt, index))
            {
                text.Append("\n**").Append(letter).Append(")** ").Append(option);
            }

            index += doubt.Options.Count;
        }

        text.Append(draft.Doubts.Count == 1
            ? "\n\nResponda só com a letra da opção (ex.: A)."
            : "\n\nResponda com uma letra para cada pergunta (ex.: A e C).");
        return text.ToString();
    }

    /// <summary>The open doubts as the AI receives them: each option with the letter the user saw.</summary>
    public static object DoubtsForPrompt(IReadOnlyList<GoalDoubt> doubts)
    {
        var index = 0;
        var result = new List<object>();
        foreach (var doubt in doubts)
        {
            result.Add(new
            {
                question = doubt.Question,
                options = Lettered(doubt, index).Select(o => new { letter = o.Letter, text = o.Option }).ToList(),
            });
            index += doubt.Options.Count;
        }

        return result;
    }

    private static string? Values(GoalDraft goal)
    {
        var unit = goal.Unit is null ? "" : " " + goal.Unit;
        return (goal.StartValue, goal.TargetValue) switch
        {
            ({ } start, { } target) => $"de {Number(start)} para {Number(target)}{unit}",
            (null, { } target) => $"{Number(target)}{unit}",
            ({ } start, null) => $"hoje {Number(start)}{unit}",
            _ => null,
        };
    }

    private static string Number(decimal value) => value.ToString("0.##", PtBr);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
