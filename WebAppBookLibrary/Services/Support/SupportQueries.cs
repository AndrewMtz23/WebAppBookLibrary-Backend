using Microsoft.Extensions.Options;
using WebAppBookLibrary.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Support;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Domain.Support;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Support;

public sealed class SupportQueries(MongoDBService mongo, IOptions<SupportOptions>? options = null)
{
    private IMongoCollection<SupportTicket> Tickets => mongo._database.GetCollection<SupportTicket>("SupportTickets");
    internal async Task<User?> ActorAsync(string id, CancellationToken ct) => ObjectId.TryParse(id, out _) ? await mongo.Users.Find(u => u.Id == id && u.IsActive).FirstOrDefaultAsync(ct) : null;
    public Task<SupportResult<PagedResult<SupportTicketSummary>>> ListMineAsync(string actorId, SupportQuery query, CancellationToken ct) => ListAsync(actorId, query, false, ct);
    public Task<SupportResult<PagedResult<SupportTicketSummary>>> ListStaffAsync(string actorId, SupportQuery query, CancellationToken ct) => ListAsync(actorId, query, true, ct);
    private async Task<SupportResult<PagedResult<SupportTicketSummary>>> ListAsync(string actorId, SupportQuery query, bool staff, CancellationToken ct)
    {
        var actor = await ActorAsync(actorId, ct);
        if (actor is null) return SupportResult<PagedResult<SupportTicketSummary>>.Fail(401, "support_account_unavailable");
        if (staff && actor.Role is not ("admin" or "librarian")) return SupportResult<PagedResult<SupportTicketSummary>>.Fail(403, "support_staff_required");
        if (!ValidPage(query.Page, query.PageSize) || query.Status != null && !SupportRules.ValidStatus(query.Status) || query.Category != null && !SupportRules.ValidCategory(query.Category))
            return SupportResult<PagedResult<SupportTicketSummary>>.Fail(400, "support_query_invalid");
        var f = Builders<SupportTicket>.Filter;
        var filter = !staff ? f.Eq(t => t.RequesterId, actorId) : actor.Role == "librarian" ? f.In(t => t.Category, new[] { "loan", "catalog" }) : f.Empty;
        if (query.Status != null) filter &= f.Eq(t => t.Status, query.Status);
        if (query.Category != null) filter &= f.Eq(t => t.Category, query.Category);
        var total = await Tickets.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await Tickets.Find(filter).SortByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Skip((query.Page - 1) * query.PageSize).Limit(query.PageSize).ToListAsync(ct);
        return new(new(items.Select(t => new SupportTicketSummary(t.Id, t.Category, t.Subject, t.Status, t.Version, t.CreatedAt, t.UpdatedAt)).ToArray(), query.Page, query.PageSize, total));
    }
    internal static bool ValidPage(int page, int size) => page > 0 && size is >= 1 and <= 100 && (long)(page - 1) * size <= int.MaxValue;
    public async Task<SupportResult<SupportTicketDetail>> GetMineAsync(string actorId, string id, int page, int pageSize, CancellationToken ct)
    {
        if (await ActorAsync(actorId, ct) is null) return SupportResult<SupportTicketDetail>.Fail(401, "support_account_unavailable");
        if (!ValidPage(page, pageSize)) return SupportResult<SupportTicketDetail>.Fail(400, "support_query_invalid");
        var ticket = ObjectId.TryParse(id, out _) ? await Tickets.Find(t => t.Id == id && t.RequesterId == actorId).FirstOrDefaultAsync(ct) : null;
        return ticket == null ? SupportResult<SupportTicketDetail>.Fail(404, "support_not_found") : new(await DetailAsync(ticket, page, pageSize, ct));
    }
    internal async Task<SupportTicketDetail> DetailAsync(SupportTicket ticket, int page, int size, CancellationToken ct)
    {
        var replies = mongo._database.GetCollection<SupportReply>("SupportReplies");
        var count = await replies.CountDocumentsAsync(r => r.TicketId == ticket.Id, cancellationToken: ct);
        var rows = await replies.Find(r => r.TicketId == ticket.Id).SortBy(r => r.CreatedAt).ThenBy(r => r.Id).Skip((page - 1) * size).Limit(size).ToListAsync(ct);
        return new(ticket.Id, ticket.Category, ticket.Subject, ticket.Description, ticket.BookId, ticket.Status, ticket.Version, ticket.CreatedAt, ticket.UpdatedAt, ticket.ResolvedAt,
            new(rows.Select(r => new SupportMessageResponse(r.Id, r.IsStaff ? r.AuthorId == null ? "Personal eliminado" : "Personal de biblioteca" : "Solicitante", r.Text, r.CreatedAt)).ToArray(), page, size, count), ticket.ResolvedAt?.AddDays(options?.Value.ReopenDays ?? 7));
    }
    public async Task<SupportResult<StaffSupportDetail>> GetStaffAsync(string actorId, string id, int page, int pageSize, CancellationToken ct)
    {
        var actor = await ActorAsync(actorId, ct);
        if (actor is null) return SupportResult<StaffSupportDetail>.Fail(401, "support_account_unavailable");
        if (actor.Role is not ("admin" or "librarian")) return SupportResult<StaffSupportDetail>.Fail(403, "support_staff_required");
        if (!ValidPage(page, pageSize)) return SupportResult<StaffSupportDetail>.Fail(400, "support_query_invalid");
        var ticket = ObjectId.TryParse(id, out _) ? await Tickets.Find(t => t.Id == id).FirstOrDefaultAsync(ct) : null;
        if (ticket == null || !SupportRules.CanAttend(actor.Role, ticket.Category)) return SupportResult<StaffSupportDetail>.Fail(404, "support_not_found");
        var notes = mongo._database.GetCollection<SupportInternalNote>("SupportInternalNotes");
        var history = mongo._database.GetCollection<SupportHistory>("SupportHistory");
        var noteRows = await notes.Find(n => n.TicketId == id).SortBy(n => n.CreatedAt).ThenBy(n => n.Id).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(ct);
        var historyRows = await history.Find(h => h.TicketId == id).SortBy(h => h.CreatedAt).ThenBy(h => h.Id).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(ct);
        return new(new(await DetailAsync(ticket, page, pageSize, ct), ticket.AssigneeId,
            new(noteRows.Select(n => new SupportMessageResponse(n.Id, n.AuthorId == null ? "Personal eliminado" : "Personal de biblioteca", n.Text, n.CreatedAt)).ToArray(), page, pageSize, await notes.CountDocumentsAsync(n => n.TicketId == id, cancellationToken: ct)),
            new(historyRows.Select(h => new SupportHistoryResponse(h.Id, h.Action, h.FromStatus, h.ToStatus, h.Reason, h.CreatedAt)).ToArray(), page, pageSize, await history.CountDocumentsAsync(h => h.TicketId == id, cancellationToken: ct))));
    }
    public async Task<SupportResult<IReadOnlyList<SupportAssignee>>> AssigneesAsync(string actorId, string category, CancellationToken ct)
    {
        var actor = await ActorAsync(actorId, ct);
        if (actor == null) return SupportResult<IReadOnlyList<SupportAssignee>>.Fail(401, "support_account_unavailable");
        if (!SupportRules.CanAttend(actor.Role, category)) return SupportResult<IReadOnlyList<SupportAssignee>>.Fail(403, "support_staff_required");
        var rows = await mongo.Users.Find(u => u.IsActive && (u.Role == "admin" || (category == "loan" || category == "catalog") && u.Role == "librarian")).SortBy(u => u.Id).Limit(100).ToListAsync(ct);
        return new(rows.Select(u => new SupportAssignee(u.Id, string.IsNullOrWhiteSpace(u.DisplayName) ? "Personal de biblioteca" : u.DisplayName)).ToArray());
    }
}
