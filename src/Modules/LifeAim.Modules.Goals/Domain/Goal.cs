namespace LifeAim.Modules.Goals.Domain;

/// <summary>
/// A goal. Only the name is required: the conversation may not have found out the rest yet,
/// and the user can add it later.
/// </summary>
internal sealed class Goal
{
    public const int MaxNameLength = 200;

    private Goal(
        Guid id,
        Guid userId,
        string name,
        string? description,
        string? indicator,
        DateOnly? dueDate,
        DateTimeOffset createdAt)
    {
        Id = id;
        UserId = userId;
        Name = name;
        Description = description;
        Indicator = indicator;
        DueDate = dueDate;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public Guid UserId { get; }
    public string Name { get; }
    public string? Description { get; }
    public string? Indicator { get; }
    public DateOnly? DueDate { get; }
    public DateTimeOffset CreatedAt { get; }

    public static Goal Create(
        Guid userId,
        string name,
        string? description,
        string? indicator,
        DateOnly? dueDate,
        DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A goal needs its user.", nameof(userId));
        }

        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName))
        {
            throw new ArgumentException("A goal needs a name.", nameof(name));
        }

        if (trimmedName.Length > MaxNameLength)
        {
            throw new ArgumentException($"A goal name has at most {MaxNameLength} characters.", nameof(name));
        }

        // Version 7 GUIDs start with a timestamp, so new rows land at the end of the primary key index.
        return new Goal(
            Guid.CreateVersion7(now),
            userId,
            trimmedName,
            Blank(description),
            Blank(indicator),
            dueDate,
            now.ToUniversalTime());
    }

    /// <summary>Rebuilds a goal read from the database (already validated when it was created).</summary>
    public static Goal Load(
        Guid id,
        Guid userId,
        string name,
        string? description,
        string? indicator,
        DateOnly? dueDate,
        DateTimeOffset createdAt) =>
        new(id, userId, name, description, indicator, dueDate, createdAt);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
