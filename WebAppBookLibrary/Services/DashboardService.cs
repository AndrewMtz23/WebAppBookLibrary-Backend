using WebAppBookLibrary.Contracts.Dashboard;

namespace WebAppBookLibrary.Services;

public sealed class DashboardService(IDashboardStore store)
{
    public async Task<ReaderDashboardResponse?> ReaderAsync(string username, DashboardPeriod period, CancellationToken token)
    {
        var user = await store.FindActiveUserAsync(username, token);
        return user is null ? null : await store.ReaderAsync(user.Id, period, token);
    }
    public Task<LibrarianDashboardResponse> LibrarianAsync(DashboardPeriod period, CancellationToken token) => store.LibrarianAsync(period, token);
    public Task<AdminDashboardResponse> AdminAsync(DashboardPeriod period, CancellationToken token) => store.AdminAsync(period, token);
}
