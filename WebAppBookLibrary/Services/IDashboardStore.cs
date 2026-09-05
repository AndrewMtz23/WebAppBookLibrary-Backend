using WebAppBookLibrary.Contracts.Dashboard;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public interface IDashboardStore
{
    Task<User?> FindActiveUserAsync(string username, CancellationToken token);
    Task<ReaderDashboardResponse> ReaderAsync(string userId, DashboardPeriod period, CancellationToken token);
    Task<LibrarianDashboardResponse> LibrarianAsync(DashboardPeriod period, CancellationToken token);
    Task<AdminDashboardResponse> AdminAsync(DashboardPeriod period, CancellationToken token);
}
