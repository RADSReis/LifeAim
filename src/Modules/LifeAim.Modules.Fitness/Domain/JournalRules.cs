using LifeAim.Modules.Fitness.Contracts;

namespace LifeAim.Modules.Fitness.Domain;

/// <summary>
/// What the journal accepts. The numbers come from a conversation read by an AI, so every value is checked here before
/// it reaches the database: a typo ("945 kg") or an invented number is refused with a message the caller can show.
/// The same limits are in the tables' CHECK constraints.
/// </summary>
internal static class JournalRules
{
    public const int MaxMealItems = 40;

    public static SaveProfileRequest Valid(SaveProfileRequest request)
    {
        Range(request.HeightCm, 80, 250, "A altura (em cm)");
        Range(request.BirthYear, 1900, 2100, "O ano de nascimento");
        Range(request.TargetWeightKg, 20, 400, "O peso alvo (em kg)");
        Range(request.DailyEnergyKcal, 800, 8000, "O gasto diário (em kcal)");
        Range(request.CalorieTargetKcal, 800, 8000, "A meta de calorias");
        Range(request.ProteinTargetG, 10, 500, "A meta de proteína (em g)");
        return request with { Notes = Text(request.Notes, 2000) };
    }

    public static RecordBodyRequest Valid(RecordBodyRequest request)
    {
        Range(request.WeightKg, 20, 400, "O peso (em kg)");
        Range(request.WaistCm, 30, 300, "A circunferência abdominal (em cm)");
        Range(request.HipCm, 30, 300, "A circunferência do quadril (em cm)");
        Range(request.ChestCm, 30, 300, "A circunferência peitoral (em cm)");
        Range(request.MuscleKg, 5, 200, "A massa muscular (em kg)");
        Range(request.FatKg, 1, 300, "A massa gorda (em kg)");
        Range(request.WaterKg, 5, 200, "A água corporal (em kg)");
        Range(request.FatPercent, 1, 80, "O percentual de gordura");
        Range(request.Bmi, 5, 100, "O IMC");
        Range(request.BmrKcal, 500, 6000, "A taxa de metabolismo basal (em kcal)");

        var note = Text(request.Note, 500);
        var hasValue = request.WeightKg ?? request.WaistCm ?? request.HipCm ?? request.ChestCm ?? request.MuscleKg
                       ?? request.FatKg ?? request.WaterKg ?? request.FatPercent ?? request.Bmi ?? request.BmrKcal;
        if (hasValue is null && note is null)
        {
            throw new ArgumentException("Um registro corporal precisa de pelo menos um valor (peso, medida...).");
        }

        return request with { Note = note };
    }

    public static string MealName(string? name) =>
        Text(name, 80) ?? throw new ArgumentException("Uma refeição precisa de um nome (\"Almoço\").");

    public static IReadOnlyList<MealItemDto> Items(IReadOnlyList<MealItemDto>? items)
    {
        if (items is not { Count: > 0 })
        {
            throw new ArgumentException("Uma refeição precisa de pelo menos um item.");
        }

        if (items.Count > MaxMealItems)
        {
            throw new ArgumentException($"Uma refeição tem no máximo {MaxMealItems} itens.");
        }

        return items.Select(Item).ToList();
    }

    private static MealItemDto Item(MealItemDto item)
    {
        var description = Text(item.Description, 200)
                          ?? throw new ArgumentException("Cada item da refeição precisa dizer o que é.");

        if (item.Quantity is <= 0)
        {
            throw new ArgumentException($"A quantidade de \"{description}\" precisa ser maior que zero.");
        }

        // The ends of a range told in the wrong order are just swapped.
        var (kcalMin, kcalMax) = Ordered(item.KcalMin, item.KcalMax);
        var (proteinMin, proteinMax) = Ordered(item.ProteinMinG, item.ProteinMaxG);
        if (kcalMin < 0 || kcalMax > 5000 || proteinMin < 0 || proteinMax > 500 || item.CarbsG is < 0 or > 1000 || item.FatG is < 0 or > 500)
        {
            throw new ArgumentException($"Os valores nutricionais de \"{description}\" estão fora do possível.");
        }

        return item with
        {
            Description = description,
            Unit = item.Quantity is null ? null : Text(item.Unit, 20),
            KcalMin = kcalMin,
            KcalMax = kcalMax,
            ProteinMinG = proteinMin,
            ProteinMaxG = proteinMax,
        };
    }

    public static AddExerciseRequest Valid(AddExerciseRequest request)
    {
        var activity = Text(request.Activity, 200) ?? throw new ArgumentException("Um exercício precisa dizer o que foi feito.");
        Range(request.DurationMinutes, 1, 1440, "A duração (em minutos)");

        // One end only means an exact value.
        var min = request.KcalMin ?? request.KcalMax;
        var max = request.KcalMax ?? request.KcalMin;
        if (min is { } low && max is { } high)
        {
            (low, high) = Ordered(low, high);
            if (low < 0 || high > 10000)
            {
                throw new ArgumentException("O gasto calórico do exercício está fora do possível.");
            }

            (min, max) = (low, high);
        }

        return request with { Activity = activity, KcalMin = min, KcalMax = max };
    }

    public static SaveFoodRequest Valid(SaveFoodRequest request)
    {
        var name = Text(request.Name, 120) ?? throw new ArgumentException("Um alimento precisa de um nome.");
        var serving = Text(request.Serving, 60)
                      ?? throw new ArgumentException("Um alimento precisa dizer a porção dos valores (\"30 g\", \"1 pote de 90 g\").");
        if (request.Kcal is < 0 or > 5000 || request.ProteinG is < 0 or > 500 || request.CarbsG is < 0 or > 1000 || request.FatG is < 0 or > 500)
        {
            throw new ArgumentException($"Os valores nutricionais de \"{name}\" estão fora do possível.");
        }

        return request with { Name = name, Serving = serving };
    }

    public static string DayNote(string? note) =>
        Text(note, 500) ?? throw new ArgumentException("Uma observação do dia precisa de um texto.");

    private static (decimal Min, decimal Max) Ordered(decimal a, decimal b) => a <= b ? (a, b) : (b, a);

    private static void Range(decimal? value, decimal min, decimal max, string what)
    {
        if (value is { } v && (v < min || v > max))
        {
            throw new ArgumentException($"{what} precisa estar entre {min:0} e {max:0}.");
        }
    }

    private static void Range(int? value, int min, int max, string what) => Range((decimal?)value, min, max, what);

    /// <summary>Trimmed, cut at <paramref name="maxLength"/>; null when blank.</summary>
    private static string? Text(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
