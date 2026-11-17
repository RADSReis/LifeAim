using LifeAim.Modules.Fitness.Contracts;
using LifeAim.Modules.Fitness.Infrastructure;

namespace LifeAim.Modules.Fitness.Application;

internal sealed class FitnessReminders(ReminderRepository repository, TimeProvider timeProvider) : IFitnessReminders
{
    public const int MaxTextLength = 120;

    public async Task<ReminderDto> SetAsync(SetReminderRequest request, CancellationToken cancellationToken = default)
    {
        var text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim();
        if (request.Kind == ReminderKind.Custom && text is null)
        {
            throw new ArgumentException("Um lembrete livre precisa dizer do que é (\"tomar a vitamina\").");
        }

        if (text?.Length > MaxTextLength)
        {
            throw new ArgumentException($"O texto do lembrete tem no máximo {MaxTextLength} caracteres.");
        }

        var at = new TimeOnly(request.At.Hour, request.At.Minute);
        var now = timeProvider.GetUtcNow();
        var valid = request with { At = at, Text = request.Kind == ReminderKind.Custom ? text : null };
        var id = await repository.SetAsync(Guid.CreateVersion7(now), valid, now, cancellationToken);
        return new ReminderDto(id, valid.Kind, at, valid.Text, false, false);
    }

    public Task<bool> RemoveAsync(Guid userId, ReminderKind kind, string? text = null, CancellationToken cancellationToken = default) =>
        repository.RemoveAsync(userId, kind, text?.Trim(), cancellationToken);

    public Task<IReadOnlyList<ReminderDto>> ListAsync(Guid userId, DateOnly day, CancellationToken cancellationToken = default) =>
        repository.ListAsync(userId, day, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListUserIdsAsync(CancellationToken cancellationToken = default) =>
        repository.ListUserIdsAsync(cancellationToken);

    public Task<bool> MarkSentAsync(Guid reminderId, Guid userId, DateOnly day, CancellationToken cancellationToken = default) =>
        repository.MarkAsync(reminderId, userId, day, done: false, timeProvider.GetUtcNow(), cancellationToken);

    public Task<bool> MarkDoneAsync(Guid reminderId, Guid userId, DateOnly day, CancellationToken cancellationToken = default) =>
        repository.MarkAsync(reminderId, userId, day, done: true, timeProvider.GetUtcNow(), cancellationToken);
}
