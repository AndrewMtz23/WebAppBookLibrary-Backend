using WebAppBookLibrary.Domain.Support;

namespace WebAppBookLibrary.Tests;

public sealed class SupportRulesTests
{
    [Theory]
    [InlineData("librarian", "account", false)]
    [InlineData("librarian", "technical", false)]
    [InlineData("librarian", "loan", true)]
    [InlineData("librarian", "catalog", true)]
    [InlineData("admin", "technical", true)]
    [InlineData("user", "catalog", false)]
    [InlineData("admin", "unknown", false)]
    public void Attendance_is_scoped_by_category(string role, string category, bool allowed) =>
        Assert.Equal(allowed, SupportRules.CanAttend(role, category));

    [Fact]
    public void Reopening_has_an_inclusive_seven_day_window()
    {
        var resolved = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(SupportRules.CanTransition("resolved", "open", true, false, resolved, resolved.AddDays(7), 7));
        Assert.False(SupportRules.CanTransition("resolved", "open", true, false, resolved, resolved.AddDays(7).AddTicks(1), 7));
        Assert.False(SupportRules.CanTransition("resolved", "open", false, true, resolved, resolved, 7));
    }

    [Theory]
    [InlineData("open", "in_progress", true)]
    [InlineData("in_progress", "resolved", true)]
    [InlineData("resolved", "closed", true)]
    [InlineData("open", "closed", false)]
    [InlineData("closed", "open", false)]
    [InlineData("open", "resolved", false)]
    public void Staff_follows_the_state_machine(string current, string next, bool allowed) =>
        Assert.Equal(allowed, SupportRules.CanTransition(current, next, false, true, null, DateTime.UtcNow, 7));

    [Theory]
    [InlineData("", 160, false)]
    [InlineData("  ", 160, false)]
    [InlineData("subject", 160, true)]
    public void Text_is_required(string value, int max, bool allowed) => Assert.Equal(allowed, SupportRules.ValidText(value, max));

    [Fact]
    public void Text_length_is_bounded()
    {
        Assert.True(SupportRules.ValidText(new string('a', 160), 160));
        Assert.False(SupportRules.ValidText(new string('a', 161), 160));
        Assert.True(SupportRules.ValidText(new string('a', 4000), 4000));
        Assert.False(SupportRules.ValidText(new string('a', 4001), 4000));
    }
}
