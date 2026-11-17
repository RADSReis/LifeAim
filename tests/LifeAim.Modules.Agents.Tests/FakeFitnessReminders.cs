using LifeAim.Modules.Fitness.Contracts;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>The journal's reminders in memory, with the rules of fitness.reminders (one per kind, free ones by text).</summary>
internal sealed class FakeFitnessReminders : IFitnessReminders
{
    public List<(Guid UserId, ReminderDto Reminder)> Reminders { get; } = [];
    public HashSet<(Guid ReminderId, DateOnly Day)> Sent { get; } = [];
    public HashSet<(Guid ReminderId, DateOnly Day)> Done { get; } = [];

    public Task<ReminderDto> SetAsync(SetReminderRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Kind == ReminderKind.Custom && string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ArgumentException("Um lembrete livre precisa dizer do que é.");
        }

        var index = Find(request.UserId, request.Kind, request.Text);
        var reminder = index < 0
            ? new ReminderDto(Guid.CreateVersion7(), request.Kind, request.At, request.Kind == ReminderKind.Custom ? request.Text!.Trim() : null, false, false)
            : Reminders[index].Reminder with { At = request.At };
        if (index < 0)
        {
            Reminders.Add((request.UserId, reminder));
        }
        else
        {
            Reminders[index] = (request.UserId, reminder);
        }

        return Task.FromResult(reminder);
    }

    public Task<bool> RemoveAsync(Guid userId, ReminderKind kind, string? text = null, CancellationToken cancellationToken = default)
    {
        var index = Find(userId, kind, text);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        Reminders.RemoveAt(index);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ReminderDto>> ListAsync(Guid userId, DateOnly day, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReminderDto>>(Reminders.Where(r => r.UserId == userId)
            .Select(r => r.Reminder with { SentToday = Sent.Contains((r.Reminder.Id, day)), DoneToday = Done.Contains((r.Reminder.Id, day)) })
            .OrderBy(r => r.At).ToList());

    public Task<IReadOnlyList<Guid>> ListUserIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(Reminders.Select(r => r.UserId).Distinct().ToList());

    public Task<bool> MarkSentAsync(Guid reminderId, Guid userId, DateOnly day, CancellationToken cancellationToken = default) =>
        Task.FromResult(Owns(reminderId, userId) && (Sent.Add((reminderId, day)) || true));

    public Task<bool> MarkDoneAsync(Guid reminderId, Guid userId, DateOnly day, CancellationToken cancellationToken = default) =>
        Task.FromResult(Owns(reminderId, userId) && (Done.Add((reminderId, day)) || true));

    private bool Owns(Guid reminderId, Guid userId) => Reminders.Any(r => r.UserId == userId && r.Reminder.Id == reminderId);

    private int Find(Guid userId, ReminderKind kind, string? text) =>
        Reminders.FindIndex(r => r.UserId == userId && r.Reminder.Kind == kind
                                 && (kind != ReminderKind.Custom || string.Equals(r.Reminder.Text, text?.Trim(), StringComparison.OrdinalIgnoreCase)));
}
