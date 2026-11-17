namespace LifeAim.Modules.Fitness.Contracts;

/// <summary>
/// The Fitness module's public API: the journal of a weight-loss goal (what was eaten, weight and measures, workouts,
/// the user's own nutrition table), kept by conversation. Other modules (Pippin's tools, in Agents) use only this.
/// The module knows neither the Goals module nor time zones: the caller says which day each thing belongs to.
/// Invalid values (a weight of 900 kg, a meal without items) throw <see cref="ArgumentException"/>.
/// </summary>
public interface IFitnessJournal
{
    Task<FitnessProfileDto?> GetProfileAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Creates or completes the profile: only the fields given (not null) change.</summary>
    Task<FitnessProfileDto> SaveProfileAsync(SaveProfileRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Weight, measures and bioimpedance of one day: the day's record is created or completed, and a value told again
    /// replaces the earlier one. Returns the record as it is now.
    /// </summary>
    Task<BodyRecordDto> RecordBodyAsync(RecordBodyRequest request, CancellationToken cancellationToken = default);

    Task<MealDto> AddMealAsync(AddMealRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Corrects a meal: the name and the time change when given, and <see cref="UpdateMealRequest.Items"/>, when
    /// given, replaces ALL its items. Null when it is not the user's meal.
    /// </summary>
    Task<MealDto?> UpdateMealAsync(UpdateMealRequest request, CancellationToken cancellationToken = default);

    Task<bool> DeleteMealAsync(Guid mealId, Guid userId, CancellationToken cancellationToken = default);

    Task<ExerciseDto> AddExerciseAsync(AddExerciseRequest request, CancellationToken cancellationToken = default);

    Task<bool> DeleteExerciseAsync(Guid exerciseId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Adds a food to the user's own table, or replaces the one with the same name (ignoring case).</summary>
    Task<FoodDto> SaveFoodAsync(SaveFoodRequest request, CancellationToken cancellationToken = default);

    /// <summary>The user's own nutrition table, by name.</summary>
    Task<IReadOnlyList<FoodDto>> ListFoodsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the reference table of common foods (TACO, values per 100 g) by the words of
    /// <paramref name="query"/>, ignoring case, accents and plurals; the best matches first.
    /// </summary>
    Task<IReadOnlyList<ReferenceFoodDto>> SearchReferenceFoodsAsync(
        string query,
        int limit = 8,
        CancellationToken cancellationToken = default);

    /// <summary>The food of the reference table with exactly this name (ignoring case and accents), or null.</summary>
    Task<ReferenceFoodDto?> FindReferenceFoodAsync(string name, CancellationToken cancellationToken = default);

    Task AddDayNoteAsync(Guid userId, DateOnly day, string note, CancellationToken cancellationToken = default);

    /// <summary>Everything recorded on that day (an empty day when nothing was).</summary>
    Task<FitnessDayDto> GetDayAsync(Guid userId, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>
    /// The days from <paramref name="from"/> to <paramref name="to"/> (inclusive) with anything recorded, oldest first.
    /// </summary>
    Task<IReadOnlyList<FitnessDayDto>> ListDaysAsync(
        Guid userId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}

/// <param name="DailyEnergyKcal">Estimated daily expenditure without the workouts (metabolism + normal activities).</param>
/// <param name="CalorieTargetKcal">How much to eat per day, when someone set a number.</param>
/// <param name="Notes">Routine, restrictions and preferences, in the user's words.</param>
/// <param name="WeightGoalId">The saved goal a weigh-in is progress of, once known.</param>
/// <param name="ExerciseGoalId">The saved goal a workout is progress of, once known.</param>
public sealed record FitnessProfileDto(
    Guid UserId,
    decimal? HeightCm,
    int? BirthYear,
    Sex? Sex,
    decimal? TargetWeightKg,
    int? DailyEnergyKcal,
    int? CalorieTargetKcal,
    int? ProteinTargetG,
    string? Notes,
    DateTimeOffset UpdatedAt,
    Guid? WeightGoalId = null,
    Guid? ExerciseGoalId = null);

public enum Sex
{
    Male,
    Female,
}

/// <summary>Null fields stay as they are.</summary>
public sealed record SaveProfileRequest(
    Guid UserId,
    decimal? HeightCm = null,
    int? BirthYear = null,
    Sex? Sex = null,
    decimal? TargetWeightKg = null,
    int? DailyEnergyKcal = null,
    int? CalorieTargetKcal = null,
    int? ProteinTargetG = null,
    string? Notes = null,
    Guid? WeightGoalId = null,
    Guid? ExerciseGoalId = null);

/// <summary>Null fields stay as they are on the day's record. At least one value is needed.</summary>
/// <param name="WaistCm">Circunferência abdominal.</param>
public sealed record RecordBodyRequest(
    Guid UserId,
    DateOnly Day,
    decimal? WeightKg = null,
    decimal? WaistCm = null,
    decimal? HipCm = null,
    decimal? ChestCm = null,
    decimal? MuscleKg = null,
    decimal? FatKg = null,
    decimal? WaterKg = null,
    decimal? FatPercent = null,
    decimal? Bmi = null,
    int? BmrKcal = null,
    string? Note = null);

public sealed record BodyRecordDto(
    DateOnly Day,
    decimal? WeightKg,
    decimal? WaistCm,
    decimal? HipCm,
    decimal? ChestCm,
    decimal? MuscleKg,
    decimal? FatKg,
    decimal? WaterKg,
    decimal? FatPercent,
    decimal? Bmi,
    int? BmrKcal,
    string? Note);

/// <summary>Where a meal item's numbers came from.</summary>
public enum NutritionSource
{
    /// <summary>The user said them ("60 kcal").</summary>
    User,

    /// <summary>The user's own nutrition table.</summary>
    FoodTable,

    /// <summary>The reference table of common foods (TACO).</summary>
    ReferenceTable,

    /// <summary>Pippin's estimate.</summary>
    Estimate,
}

/// <summary>
/// One thing eaten. Calories and protein are a range, because an estimate has a doubt ("~510–540 kcal"); an exact
/// value has min = max.
/// </summary>
/// <param name="Description">"Pão francês", "Frango cozido com osso".</param>
/// <param name="Quantity">50, with <paramref name="Unit"/> "g"; null when the user gave no amount.</param>
public sealed record MealItemDto(
    string Description,
    decimal? Quantity,
    string? Unit,
    decimal KcalMin,
    decimal KcalMax,
    decimal ProteinMinG,
    decimal ProteinMaxG,
    decimal? CarbsG,
    decimal? FatG,
    NutritionSource Source);

/// <param name="Day">The food day (a ceia after midnight belongs to the day before).</param>
/// <param name="EatenAt">Null while the user has not said the time.</param>
public sealed record AddMealRequest(
    Guid UserId,
    DateOnly Day,
    TimeOnly? EatenAt,
    string Name,
    IReadOnlyList<MealItemDto> Items);

/// <summary>Null fields stay as they are.</summary>
public sealed record UpdateMealRequest(
    Guid MealId,
    Guid UserId,
    string? Name = null,
    TimeOnly? EatenAt = null,
    IReadOnlyList<MealItemDto>? Items = null);

public sealed record MealDto(
    Guid Id,
    DateOnly Day,
    TimeOnly? EatenAt,
    string Name,
    IReadOnlyList<MealItemDto> Items)
{
    public decimal KcalMin => Items.Sum(i => i.KcalMin);
    public decimal KcalMax => Items.Sum(i => i.KcalMax);
    public decimal ProteinMinG => Items.Sum(i => i.ProteinMinG);
    public decimal ProteinMaxG => Items.Sum(i => i.ProteinMaxG);
}

/// <param name="KcalMin">Null (with <paramref name="KcalMax"/>) when the expenditure is unknown.</param>
/// <param name="Measured">True when the calories came from a device (smartwatch), false for an estimate.</param>
public sealed record AddExerciseRequest(
    Guid UserId,
    DateOnly Day,
    TimeOnly? DoneAt,
    string Activity,
    int? DurationMinutes,
    decimal? KcalMin,
    decimal? KcalMax,
    bool Measured);

public sealed record ExerciseDto(
    Guid Id,
    DateOnly Day,
    TimeOnly? DoneAt,
    string Activity,
    int? DurationMinutes,
    decimal? KcalMin,
    decimal? KcalMax,
    bool Measured);

/// <param name="Serving">The portion the numbers are for, as on the label ("30 g (1 scoop)", "1 pote de 90 g").</param>
public sealed record SaveFoodRequest(
    Guid UserId,
    string Name,
    string Serving,
    decimal Kcal,
    decimal? ProteinG = null,
    decimal? CarbsG = null,
    decimal? FatG = null);

/// <summary>A food of the reference table (TACO): the values are per 100 g of the edible part.</summary>
/// <param name="Name">As in the table, which tells raw from cooked ("Arroz, tipo 1, cozido").</param>
public sealed record ReferenceFoodDto(
    string Name,
    string Category,
    decimal KcalPer100g,
    decimal? ProteinG,
    decimal? CarbsG,
    decimal? FatG,
    decimal? FiberG);

public sealed record FoodDto(Guid Id, string Name, string Serving, decimal Kcal, decimal? ProteinG, decimal? CarbsG, decimal? FatG);

/// <summary>One day of the journal, with its totals already added up (so nobody sums by hand).</summary>
/// <param name="Meals">In the order they were eaten (the ones without a time last).</param>
public sealed record FitnessDayDto(
    DateOnly Day,
    BodyRecordDto? Body,
    IReadOnlyList<MealDto> Meals,
    IReadOnlyList<ExerciseDto> Exercises,
    IReadOnlyList<string> Notes)
{
    public decimal KcalMin => Meals.Sum(m => m.KcalMin);
    public decimal KcalMax => Meals.Sum(m => m.KcalMax);
    public decimal ProteinMinG => Meals.Sum(m => m.ProteinMinG);
    public decimal ProteinMaxG => Meals.Sum(m => m.ProteinMaxG);
    public decimal ExerciseKcalMin => Exercises.Sum(e => e.KcalMin ?? 0);
    public decimal ExerciseKcalMax => Exercises.Sum(e => e.KcalMax ?? 0);

    public bool IsEmpty => Body is null && Meals.Count == 0 && Exercises.Count == 0 && Notes.Count == 0;
}
