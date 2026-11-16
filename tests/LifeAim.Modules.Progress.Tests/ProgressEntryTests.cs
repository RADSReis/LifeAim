using LifeAim.Modules.Progress.Domain;

namespace LifeAim.Modules.Progress.Tests;

public class ProgressEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    [Fact]
    public void An_entry_keeps_what_was_done_trimmed()
    {
        var entry = ProgressEntry.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Today, "  leu 10 páginas ", 10m, " páginas ", Now);

        Assert.Equal(("leu 10 páginas", 10m, "páginas"), (entry.Note, entry.Amount!.Value, entry.Unit!));
        Assert.Equal(Today, entry.OccurredOn);
    }

    [Fact]
    public void A_unit_without_an_amount_is_dropped()
    {
        var entry = ProgressEntry.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Today, "foi à academia", null, "vezes", Now);

        Assert.Null(entry.Unit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_entry_needs_to_say_what_was_done(string note) =>
        Assert.Throws<ArgumentException>(() =>
            ProgressEntry.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Today, note, null, null, Now));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void An_amount_must_be_positive(int amount) =>
        Assert.Throws<ArgumentException>(() =>
            ProgressEntry.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Today, "correu", amount, "km", Now));

    [Fact]
    public void An_entry_needs_its_user_and_goal()
    {
        Assert.Throws<ArgumentException>(() => ProgressEntry.Create(Guid.Empty, Guid.CreateVersion7(), Today, "x", null, null, Now));
        Assert.Throws<ArgumentException>(() => ProgressEntry.Create(Guid.CreateVersion7(), Guid.Empty, Today, "x", null, null, Now));
    }

    [Fact]
    public void A_long_note_is_cut_to_the_limit()
    {
        var entry = ProgressEntry.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Today, new string('a', 600), null, null, Now);

        Assert.Equal(ProgressEntry.MaxNoteLength, entry.Note.Length);
    }
}
