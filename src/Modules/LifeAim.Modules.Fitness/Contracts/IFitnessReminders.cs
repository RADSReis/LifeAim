namespace LifeAim.Modules.Fitness.Contracts;

/// <summary>
/// The reminders of the journal (public API): which ones each user switched on, and what happened to each one on each
/// food day. The module only keeps them; deciding when one is due and sending it is the caller's job (Agents).
/// </summary>
public interface IFitnessReminders
{
    /// <summary>
    /// Switches a reminder on, or changes its time. There is one of each built-in kind per user; a
    /// <see cref="ReminderKind.Custom"/> one is told apart by its text (ignoring case).
    /// </summary>
    Task<ReminderDto> SetAsync(SetReminderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Switches a reminder off. False when the user had no such reminder.</summary>
    Task<bool> RemoveAsync(Guid userId, ReminderKind kind, string? text = null, CancellationToken cancellationToken = default);

    /// <summary>The user's reminders, earliest first, with what happened to each on <paramref name="day"/>.</summary>
    Task<IReadOnlyList<ReminderDto>> ListAsync(Guid userId, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>The users with at least one reminder.</summary>
    Task<IReadOnlyList<Guid>> ListUserIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Records that the reminder was sent on that day. False when it is not the user's reminder.</summary>
    Task<bool> MarkSentAsync(Guid reminderId, Guid userId, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that what the reminder asks was done on that day ("tomei a vitamina"), sent or not: it is not sent
    /// again that day. False when it is not the user's reminder.
    /// </summary>
    Task<bool> MarkDoneAsync(Guid reminderId, Guid userId, DateOnly day, CancellationToken cancellationToken = default);
}

public enum ReminderKind
{
    /// <summary>"Já se pesou hoje?", when there is no weight on the day yet.</summary>
    WeighIn,

    /// <summary>"Ainda não registrei nenhuma refeição hoje", when there is none yet.</summary>
    NoRecord,

    /// <summary>The summary of the day, with its totals and balance, when anything was recorded.</summary>
    DayClose,

    /// <summary>A free reminder the user asked for ("tomar a vitamina"), until they say it is done.</summary>
    Custom,
}

/// <param name="At">The local time to send it.</param>
/// <param name="Text">Only for <see cref="ReminderKind.Custom"/>: what to remind of ("tomar a vitamina").</param>
public sealed record SetReminderRequest(Guid UserId, ReminderKind Kind, TimeOnly At, string? Text = null);

/// <param name="SentToday">It was already sent on the day asked about.</param>
/// <param name="DoneToday">The user said it was done on the day asked about.</param>
public sealed record ReminderDto(Guid Id, ReminderKind Kind, TimeOnly At, string? Text, bool SentToday, bool DoneToday);
