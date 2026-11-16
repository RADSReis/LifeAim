namespace LifeAim.Modules.Progress.Domain;

/// <summary>
/// One thing the user reported having done for a goal. Append-only: a report is a fact of that day, so it is
/// never edited; a mistake is fixed with a new report (and, later, a way to remove one).
/// </summary>
internal sealed class ProgressEntry
{
    public const int MaxNoteLength = 500;
    public const int MaxUnitLength = 30;

    private ProgressEntry(
        Guid id,
        Guid userId,
        Guid goalId,
        DateOnly occurredOn,
        decimal? amount,
        string? unit,
        string note,
        DateTimeOffset createdAt)
    {
        Id = id;
        UserId = userId;
        GoalId = goalId;
        OccurredOn = occurredOn;
        Amount = amount;
        Unit = unit;
        Note = note;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public Guid UserId { get; }
    public Guid GoalId { get; }
    public DateOnly OccurredOn { get; }
    public decimal? Amount { get; }
    public string? Unit { get; }
    public string Note { get; }
    public DateTimeOffset CreatedAt { get; }

    public static ProgressEntry Create(
        Guid userId,
        Guid goalId,
        DateOnly occurredOn,
        string note,
        decimal? amount,
        string? unit,
        DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A progress entry needs its user.", nameof(userId));
        }

        if (goalId == Guid.Empty)
        {
            throw new ArgumentException("A progress entry needs its goal.", nameof(goalId));
        }

        if (amount is <= 0)
        {
            throw new ArgumentException("An amount, when given, is greater than zero.", nameof(amount));
        }

        var trimmedUnit = Blank(unit);
        if (trimmedUnit?.Length > MaxUnitLength)
        {
            throw new ArgumentException($"A unit has at most {MaxUnitLength} characters.", nameof(unit));
        }

        // A unit without an amount says nothing.
        return new ProgressEntry(
            Guid.CreateVersion7(now),
            userId,
            goalId,
            occurredOn,
            amount,
            amount is null ? null : trimmedUnit,
            ValidNote(note),
            ToDatabasePrecision(now));
    }

    /// <summary>Rebuilds an entry read from the database (already validated when it was created).</summary>
    public static ProgressEntry Load(
        Guid id,
        Guid userId,
        Guid goalId,
        DateOnly occurredOn,
        decimal? amount,
        string? unit,
        string note,
        DateTimeOffset createdAt) =>
        new(id, userId, goalId, occurredOn, amount, unit, note, createdAt);

    private static string ValidNote(string? note)
    {
        var trimmed = Blank(note);
        if (trimmed is null)
        {
            throw new ArgumentException("A progress entry says what was done.", nameof(note));
        }

        return trimmed.Length > MaxNoteLength ? trimmed[..MaxNoteLength] : trimmed;
    }

    // timestamptz keeps microseconds; .NET keeps 100 ns ticks (the same rule as Goals).
    private static DateTimeOffset ToDatabasePrecision(DateTimeOffset moment)
    {
        var utc = moment.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
