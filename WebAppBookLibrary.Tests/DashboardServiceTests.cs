using WebAppBookLibrary.Contracts.Dashboard;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class DashboardServiceTests
{
    [Fact]
    public void DashboardPeriod_RejectsRangesLongerThan366Days()
    {
        var result = DashboardPeriod.TryCreate(new DateOnly(2025, 1, 1), new DateOnly(2026, 2, 1), "UTC", out _, out var error);
        Assert.False(result);
        Assert.Equal("range_too_large", error);
    }

    [Fact]
    public void DashboardPeriod_RejectsUnknownTimezone()
    {
        var result = DashboardPeriod.TryCreate(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), "Mars/Olympus", out _, out var error);
        Assert.False(result);
        Assert.Equal("invalid_timezone", error);
    }

    [Fact]
    public async Task ReaderAsync_UsesResolvedUserAndReturnsConciliableMediaTotals()
    {
        var store = new FakeDashboardStore();
        var service = new DashboardService(store);
        DashboardPeriod.TryCreate(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 5), "UTC", out var period, out _);

        var response = await service.ReaderAsync("ana", period!, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("u1", store.ReaderUserId);
        Assert.Equal(response.TotalReservations, response.ByMedia.Sum(item => item.Count));
    }

    private sealed class FakeDashboardStore : IDashboardStore
    {
        public string? ReaderUserId { get; private set; }
        public Task<User?> FindActiveUserAsync(string username, CancellationToken token) => Task.FromResult<User?>(new User { Id = "u1", Username = username, IsActive = true });
        public Task<ReaderDashboardResponse> ReaderAsync(string userId, DashboardPeriod period, CancellationToken token) { ReaderUserId = userId; return Task.FromResult(new ReaderDashboardResponse(period.GeneratedAt, period.FromUtc, period.ToUtc, 3, 1, [new("physical", 2), new("digital", 1)])); }
        public Task<LibrarianDashboardResponse> LibrarianAsync(DashboardPeriod period, CancellationToken token) => throw new NotImplementedException();
        public Task<AdminDashboardResponse> AdminAsync(DashboardPeriod period, CancellationToken token) => throw new NotImplementedException();
    }
}
