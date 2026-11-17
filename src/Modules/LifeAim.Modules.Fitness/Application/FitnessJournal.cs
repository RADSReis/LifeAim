using LifeAim.Modules.Fitness.Contracts;
using LifeAim.Modules.Fitness.Domain;
using LifeAim.Modules.Fitness.Infrastructure;

namespace LifeAim.Modules.Fitness.Application;

internal sealed class FitnessJournal(FitnessRepository repository, TimeProvider timeProvider) : IFitnessJournal
{
    public Task<FitnessProfileDto?> GetProfileAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.GetProfileAsync(userId, cancellationToken);

    public Task<FitnessProfileDto> SaveProfileAsync(SaveProfileRequest request, CancellationToken cancellationToken = default) =>
        repository.SaveProfileAsync(JournalRules.Valid(request), Now(), cancellationToken);

    public Task<BodyRecordDto> RecordBodyAsync(RecordBodyRequest request, CancellationToken cancellationToken = default) =>
        repository.SaveBodyAsync(JournalRules.Valid(request), Now(), cancellationToken);

    public async Task<MealDto> AddMealAsync(AddMealRequest request, CancellationToken cancellationToken = default)
    {
        var now = Now();
        var meal = new MealDto(
            Guid.CreateVersion7(now), request.Day, request.EatenAt, JournalRules.MealName(request.Name), JournalRules.Items(request.Items));
        await repository.AddMealAsync(request.UserId, meal, now, cancellationToken);
        return meal;
    }

    public Task<MealDto?> UpdateMealAsync(UpdateMealRequest request, CancellationToken cancellationToken = default) =>
        repository.UpdateMealAsync(
            request with
            {
                Name = request.Name is null ? null : JournalRules.MealName(request.Name),
                Items = request.Items is null ? null : JournalRules.Items(request.Items),
            },
            cancellationToken);

    public Task<bool> DeleteMealAsync(Guid mealId, Guid userId, CancellationToken cancellationToken = default) =>
        repository.DeleteMealAsync(mealId, userId, cancellationToken);

    public async Task<ExerciseDto> AddExerciseAsync(AddExerciseRequest request, CancellationToken cancellationToken = default)
    {
        var valid = JournalRules.Valid(request);
        var now = Now();
        var exercise = new ExerciseDto(
            Guid.CreateVersion7(now), valid.Day, valid.DoneAt, valid.Activity, valid.DurationMinutes, valid.KcalMin, valid.KcalMax,
            valid.Measured);
        await repository.AddExerciseAsync(valid.UserId, exercise, now, cancellationToken);
        return exercise;
    }

    public Task<bool> DeleteExerciseAsync(Guid exerciseId, Guid userId, CancellationToken cancellationToken = default) =>
        repository.DeleteExerciseAsync(exerciseId, userId, cancellationToken);

    public Task<FoodDto> SaveFoodAsync(SaveFoodRequest request, CancellationToken cancellationToken = default)
    {
        var now = Now();
        return repository.SaveFoodAsync(Guid.CreateVersion7(now), JournalRules.Valid(request), now, cancellationToken);
    }

    public Task<IReadOnlyList<FoodDto>> ListFoodsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.ListFoodsAsync(userId, cancellationToken);

    public async Task<IReadOnlyList<ReferenceFoodDto>> SearchReferenceFoodsAsync(
        string query,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        var words = FoodSearch.Words(query);
        return words.Count == 0 ? [] : await repository.SearchReferenceFoodsAsync(words, Math.Clamp(limit, 1, 20), cancellationToken);
    }

    public async Task<ReferenceFoodDto?> FindReferenceFoodAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalized = FoodSearch.Normalize(name);
        return normalized.Length == 0 ? null : await repository.FindReferenceFoodAsync(normalized, cancellationToken);
    }

    public Task AddDayNoteAsync(Guid userId, DateOnly day, string note, CancellationToken cancellationToken = default)
    {
        var now = Now();
        return repository.AddDayNoteAsync(Guid.CreateVersion7(now), userId, day, JournalRules.DayNote(note), now, cancellationToken);
    }

    public async Task<FitnessDayDto> GetDayAsync(Guid userId, DateOnly day, CancellationToken cancellationToken = default) =>
        (await ListDaysAsync(userId, day, day, cancellationToken)).FirstOrDefault() ?? new FitnessDayDto(day, null, [], [], []);

    public async Task<IReadOnlyList<FitnessDayDto>> ListDaysAsync(
        Guid userId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var bodies = await repository.ListBodyAsync(userId, from, to, cancellationToken);
        var meals = await repository.ListMealsAsync(userId, from, to, cancellationToken);
        var exercises = await repository.ListExercisesAsync(userId, from, to, cancellationToken);
        var notes = await repository.ListDayNotesAsync(userId, from, to, cancellationToken);

        return bodies.Select(b => b.Day)
            .Concat(meals.Select(m => m.Day))
            .Concat(exercises.Select(e => e.Day))
            .Concat(notes.Select(n => n.Day))
            .Distinct()
            .Order()
            .Select(day => new FitnessDayDto(
                day,
                bodies.FirstOrDefault(b => b.Day == day),
                meals.Where(m => m.Day == day).ToList(),
                exercises.Where(e => e.Day == day).ToList(),
                notes.Where(n => n.Day == day).Select(n => n.Note).ToList()))
            .ToList();
    }

    // timestamptz keeps microseconds; .NET keeps 100 ns ticks (the same rule as Goals).
    private DateTimeOffset Now()
    {
        var utc = timeProvider.GetUtcNow().ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }
}
