using RioEditor.Core.Settings;
using RioEditor.Core.Sponsorship;
using RioEditor.Core.Storage;
using Xunit;

namespace RioEditor.Core.Tests;

/// <summary>
/// The prompt asks a favour of someone who already chose to use the app, so the thresholds matter
/// more than most logic: too eager and a free tool becomes a nuisance. A fake clock makes the
/// fortnight-long quiet periods testable in milliseconds.
/// </summary>
public class SponsorPolicyTests
{
    private sealed class MemoryStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> _map = new();

        public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
            ValueTask.FromResult(_map.GetValueOrDefault(key));

        public ValueTask SetAsync(string key, string value, CancellationToken ct = default)
        {
            _map[key] = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken ct = default)
        {
            _map.Remove(key);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static async Task<(SponsorPolicy policy, FakeClock clock, ISettingsService settings)> NewAsync()
    {
        var settings = new SettingsService(new MemoryStore());
        await settings.LoadAsync();
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        return (new SponsorPolicy(settings, clock), clock, settings);
    }

    /// <summary>Simulates a user who opens the app and saves work on each of <paramref name="days"/> days.</summary>
    private static async Task UseForAsync(SponsorPolicy policy, FakeClock clock, int days, bool saving = true)
    {
        for (var i = 0; i < days; i++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            await policy.RecordLaunchAsync();
            if (saving)
            {
                await policy.RecordSaveAsync();
            }
        }
    }

    [Fact]
    public async Task Never_prompts_on_a_first_launch()
    {
        var (policy, _, _) = await NewAsync();
        await policy.RecordLaunchAsync();

        Assert.False(policy.ShouldPrompt());
    }

    [Fact]
    public async Task Never_prompts_inside_the_opening_fortnight_however_heavy_the_use()
    {
        var (policy, clock, _) = await NewAsync();

        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromHours(6));
            await policy.RecordLaunchAsync();
            await policy.RecordSaveAsync();
        }

        Assert.False(policy.ShouldPrompt());
    }

    [Fact]
    public async Task Never_prompts_someone_who_has_not_saved_anything()
    {
        // Opening the app repeatedly is not the same as using it; someone who only ever read the
        // welcome document should never be asked.
        var (policy, clock, _) = await NewAsync();
        await UseForAsync(policy, clock, days: 30, saving: false);

        Assert.False(policy.ShouldPrompt());
    }

    [Fact]
    public async Task Prompts_a_settled_user()
    {
        var (policy, clock, _) = await NewAsync();
        await UseForAsync(policy, clock, days: 20);

        Assert.True(policy.ShouldPrompt());
    }

    [Fact]
    public async Task Stays_quiet_for_two_months_after_asking()
    {
        var (policy, clock, _) = await NewAsync();
        await UseForAsync(policy, clock, days: 20);
        Assert.True(policy.ShouldPrompt());

        await policy.RecordPromptShownAsync();
        Assert.False(policy.ShouldPrompt());

        clock.Advance(TimeSpan.FromDays(30));
        await policy.RecordLaunchAsync();
        Assert.False(policy.ShouldPrompt());

        clock.Advance(TimeSpan.FromDays(31));
        await policy.RecordLaunchAsync();
        Assert.True(policy.ShouldPrompt());
    }

    [Fact]
    public async Task Never_asks_more_than_three_times()
    {
        var (policy, clock, _) = await NewAsync();
        await UseForAsync(policy, clock, days: 20);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(policy.ShouldPrompt());
            await policy.RecordPromptShownAsync();
            clock.Advance(TimeSpan.FromDays(61));
            await policy.RecordLaunchAsync();
        }

        Assert.False(policy.ShouldPrompt());

        clock.Advance(TimeSpan.FromDays(365));
        await policy.RecordLaunchAsync();
        Assert.False(policy.ShouldPrompt());
    }

    [Fact]
    public async Task Dismissal_is_permanent()
    {
        var (policy, clock, _) = await NewAsync();
        await UseForAsync(policy, clock, days: 20);
        Assert.True(policy.ShouldPrompt());

        await policy.DismissForeverAsync();

        clock.Advance(TimeSpan.FromDays(500));
        await policy.RecordLaunchAsync();
        Assert.False(policy.ShouldPrompt());
    }

    [Fact]
    public async Task Counts_a_day_once_however_often_the_app_is_opened()
    {
        var (policy, clock, settings) = await NewAsync();

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            await policy.RecordLaunchAsync();
        }

        Assert.Equal(1, settings.Current.Sponsor.ActiveDays);
        Assert.Equal(5, settings.Current.Sponsor.LaunchCount);
    }

    [Fact]
    public void Points_at_the_projects_own_sponsorship_page()
    {
        var policy = new SponsorPolicy(new SettingsService(new MemoryStore()));

        Assert.Equal("https://github.com/sponsors/antargyan", policy.SponsorUri.ToString());
    }
}
