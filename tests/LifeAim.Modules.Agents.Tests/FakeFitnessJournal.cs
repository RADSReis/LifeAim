using LifeAim.Modules.Fitness.Contracts;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>
/// The Fitness module's contract in memory, with the journal's rules that matter to the conversation: one body record
/// per day (completed, not duplicated), meals in the order they were eaten, the food table by name.
/// </summary>
internal sealed class FakeFitnessJournal : IFitnessJournal
{
    public Dictionary<Guid, FitnessProfileDto> Profiles { get; } = [];
    public List<(Guid UserId, BodyRecordDto Record)> Bodies { get; } = [];
    public List<(Guid UserId, MealDto Meal)> Meals { get; } = [];
    public List<(Guid UserId, ExerciseDto Exercise)> Exercises { get; } = [];
    public List<(Guid UserId, FoodDto Food)> Foods { get; } = [];
    public List<(Guid UserId, DateOnly Day, string Note)> Notes { get; } = [];

    public Task<FitnessProfileDto?> GetProfileAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Profiles.GetValueOrDefault(userId));

    public Task<FitnessProfileDto> SaveProfileAsync(SaveProfileRequest request, CancellationToken cancellationToken = default)
    {
        var old = Profiles.GetValueOrDefault(request.UserId)
                  ?? new FitnessProfileDto(request.UserId, null, null, null, null, null, null, null, null, DateTimeOffset.UtcNow);
        return Task.FromResult(Profiles[request.UserId] = old with
        {
            HeightCm = request.HeightCm ?? old.HeightCm,
            BirthYear = request.BirthYear ?? old.BirthYear,
            Sex = request.Sex ?? old.Sex,
            TargetWeightKg = request.TargetWeightKg ?? old.TargetWeightKg,
            DailyEnergyKcal = request.DailyEnergyKcal ?? old.DailyEnergyKcal,
            CalorieTargetKcal = request.CalorieTargetKcal ?? old.CalorieTargetKcal,
            ProteinTargetG = request.ProteinTargetG ?? old.ProteinTargetG,
            Notes = request.Notes ?? old.Notes,
            WeightGoalId = request.WeightGoalId ?? old.WeightGoalId,
            ExerciseGoalId = request.ExerciseGoalId ?? old.ExerciseGoalId,
        });
    }

    public Task<BodyRecordDto> RecordBodyAsync(RecordBodyRequest request, CancellationToken cancellationToken = default)
    {
        if (request.WeightKg is < 20 or > 400)
        {
            throw new ArgumentException("O peso (em kg) precisa estar entre 20 e 400.");
        }

        var index = Bodies.FindIndex(b => b.UserId == request.UserId && b.Record.Day == request.Day);
        var old = index < 0
            ? new BodyRecordDto(request.Day, null, null, null, null, null, null, null, null, null, null, null)
            : Bodies[index].Record;
        var record = old with
        {
            WeightKg = request.WeightKg ?? old.WeightKg,
            WaistCm = request.WaistCm ?? old.WaistCm,
            HipCm = request.HipCm ?? old.HipCm,
            ChestCm = request.ChestCm ?? old.ChestCm,
            MuscleKg = request.MuscleKg ?? old.MuscleKg,
            FatKg = request.FatKg ?? old.FatKg,
            WaterKg = request.WaterKg ?? old.WaterKg,
            FatPercent = request.FatPercent ?? old.FatPercent,
            Bmi = request.Bmi ?? old.Bmi,
            BmrKcal = request.BmrKcal ?? old.BmrKcal,
            Note = request.Note ?? old.Note,
        };

        if (index < 0)
        {
            Bodies.Add((request.UserId, record));
        }
        else
        {
            Bodies[index] = (request.UserId, record);
        }

        return Task.FromResult(record);
    }

    public Task<MealDto> AddMealAsync(AddMealRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Items.Count == 0)
        {
            throw new ArgumentException("Uma refeição precisa de pelo menos um item.");
        }

        var meal = new MealDto(Guid.CreateVersion7(), request.Day, request.EatenAt, request.Name.Trim(), request.Items);
        Meals.Add((request.UserId, meal));
        return Task.FromResult(meal);
    }

    public Task<MealDto?> UpdateMealAsync(UpdateMealRequest request, CancellationToken cancellationToken = default)
    {
        var index = Meals.FindIndex(m => m.UserId == request.UserId && m.Meal.Id == request.MealId);
        if (index < 0)
        {
            return Task.FromResult<MealDto?>(null);
        }

        var old = Meals[index].Meal;
        var meal = old with { Name = request.Name ?? old.Name, EatenAt = request.EatenAt ?? old.EatenAt, Items = request.Items ?? old.Items };
        Meals[index] = (request.UserId, meal);
        return Task.FromResult<MealDto?>(meal);
    }

    public Task<bool> DeleteMealAsync(Guid mealId, Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Meals.RemoveAll(m => m.UserId == userId && m.Meal.Id == mealId) > 0);

    public Task<ExerciseDto> AddExerciseAsync(AddExerciseRequest request, CancellationToken cancellationToken = default)
    {
        var exercise = new ExerciseDto(
            Guid.CreateVersion7(), request.Day, request.DoneAt, request.Activity.Trim(), request.DurationMinutes,
            request.KcalMin ?? request.KcalMax, request.KcalMax ?? request.KcalMin, request.Measured);
        Exercises.Add((request.UserId, exercise));
        return Task.FromResult(exercise);
    }

    public Task<bool> DeleteExerciseAsync(Guid exerciseId, Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Exercises.RemoveAll(e => e.UserId == userId && e.Exercise.Id == exerciseId) > 0);

    public Task<FoodDto> SaveFoodAsync(SaveFoodRequest request, CancellationToken cancellationToken = default)
    {
        Foods.RemoveAll(f => f.UserId == request.UserId && string.Equals(f.Food.Name, request.Name, StringComparison.OrdinalIgnoreCase));
        var food = new FoodDto(Guid.CreateVersion7(), request.Name, request.Serving, request.Kcal, request.ProteinG, request.CarbsG, request.FatG);
        Foods.Add((request.UserId, food));
        return Task.FromResult(food);
    }

    public Task<IReadOnlyList<FoodDto>> ListFoodsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FoodDto>>(Foods.Where(f => f.UserId == userId).Select(f => f.Food).OrderBy(f => f.Name).ToList());

    /// <summary>The reference table the tests put here.</summary>
    public List<ReferenceFoodDto> Reference { get; } = [];

    public Task<IReadOnlyList<ReferenceFoodDto>> SearchReferenceFoodsAsync(
        string query, int limit = 8, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReferenceFoodDto>>(Reference
            .Where(f => query.Split(' ').All(word => f.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Take(limit).ToList());

    public Task<ReferenceFoodDto?> FindReferenceFoodAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Reference.FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task AddDayNoteAsync(Guid userId, DateOnly day, string note, CancellationToken cancellationToken = default)
    {
        Notes.Add((userId, day, note.Trim()));
        return Task.CompletedTask;
    }

    public async Task<FitnessDayDto> GetDayAsync(Guid userId, DateOnly day, CancellationToken cancellationToken = default) =>
        (await ListDaysAsync(userId, day, day, cancellationToken)).FirstOrDefault() ?? new FitnessDayDto(day, null, [], [], []);

    public Task<IReadOnlyList<FitnessDayDto>> ListDaysAsync(
        Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        bool In(DateOnly day) => day >= from && day <= to;
        var days = Bodies.Where(b => b.UserId == userId).Select(b => b.Record.Day)
            .Concat(Meals.Where(m => m.UserId == userId).Select(m => m.Meal.Day))
            .Concat(Exercises.Where(e => e.UserId == userId).Select(e => e.Exercise.Day))
            .Concat(Notes.Where(n => n.UserId == userId).Select(n => n.Day))
            .Where(In).Distinct().Order();

        return Task.FromResult<IReadOnlyList<FitnessDayDto>>(days.Select(day => new FitnessDayDto(
            day,
            Bodies.Where(b => b.UserId == userId && b.Record.Day == day).Select(b => b.Record).FirstOrDefault(),
            Meals.Where(m => m.UserId == userId && m.Meal.Day == day).Select(m => m.Meal)
                .OrderBy(m => m.EatenAt is null)
                .ThenBy(m => m.EatenAt is { } at && FoodDay.IsLateNight(at))
                .ThenBy(m => m.EatenAt)
                .ToList(),
            Exercises.Where(e => e.UserId == userId && e.Exercise.Day == day).Select(e => e.Exercise).ToList(),
            Notes.Where(n => n.UserId == userId && n.Day == day).Select(n => n.Note).ToList())).ToList());
    }
}
