using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Support;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Domain.Support;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Support;

public sealed class MongoSupportStore(MongoDBService mongo, IOptions<SupportOptions> options, TimeProvider clock)
{
    private IMongoCollection<SupportTicket> Tickets => mongo._database.GetCollection<SupportTicket>("SupportTickets");
    private IMongoCollection<SupportReply> Replies => mongo._database.GetCollection<SupportReply>("SupportReplies");
    private IMongoCollection<SupportInternalNote> Notes => mongo._database.GetCollection<SupportInternalNote>("SupportInternalNotes");
    private IMongoCollection<SupportHistory> History => mongo._database.GetCollection<SupportHistory>("SupportHistory");
    private SupportOptions Options => options.Value;
    private DateTime Now => new BsonDateTime(clock.GetUtcNow().UtcDateTime).ToUniversalTime();

    private async Task<SupportResult<T>> TransactAsync<T>(string actorId, Func<IClientSessionHandle, User, CancellationToken, Task<SupportResult<T>>> action, CancellationToken ct)
    {
        if (!ObjectId.TryParse(actorId, out _)) return SupportResult<T>.Fail(401, "support_account_unavailable");
        try
        {
            using var session = await mongo._database.Client.StartSessionAsync(cancellationToken: ct);
            return await session.WithTransactionAsync(async (tx, token) => {
                var actor = await mongo.Users.Find(tx, u => u.Id == actorId && u.IsActive).FirstOrDefaultAsync(token);
                if (actor == null) return SupportResult<T>.Fail(401, "support_account_unavailable");
                // Also serializes quotas and permissions with role changes and account deletion.
                await UserReferenceGuard.TouchAsync(mongo.Users, tx, actorId, token);
                return await action(tx, actor, token);
            }, new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), ct);
        }
        catch (UserReferenceUnavailableException) { return SupportResult<T>.Fail(401, "support_account_unavailable"); }
        catch (MongoException ex) when (ex is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } || ex is MongoCommandException { Code: 11000 })
        { return SupportResult<T>.Fail(409, "support_conflict"); }
    }

    public Task<SupportResult<SupportTicketDetail>> CreateAsync(string actorId, CreateSupportTicket request, CancellationToken ct) => TransactAsync<SupportTicketDetail>(actorId, async (tx, actor, token) => {
        if (!SupportRules.ValidCategory(request.Category) || !SupportRules.ValidText(request.Subject, 160) || !SupportRules.ValidText(request.Description, 4000) || !Guid.TryParse(request.IdempotencyKey, out var key) ||
            request.BookId != null && !ObjectId.TryParse(request.BookId, out _) || request.LoanId != null && !ObjectId.TryParse(request.LoanId, out _))
            return SupportResult<SupportTicketDetail>.Fail(400, "support_input_invalid");
        var canonicalKey = key.ToString();
        var existing = await Tickets.Find(tx, t => t.RequesterId == actorId && t.IdempotencyKey == canonicalKey).FirstOrDefaultAsync(token);
        if (existing != null)
            return existing.Subject == request.Subject.Trim() && existing.Description == request.Description.Trim() && existing.Category == request.Category && existing.BookId == request.BookId && existing.LoanId == request.LoanId
                ? new(ToDetail(existing)) : SupportResult<SupportTicketDetail>.Fail(409, "support_idempotency_conflict");
        if (!Options.AcceptNewTickets) return SupportResult<SupportTicketDetail>.Fail(409, "support_intake_disabled");
        if (request.BookId != null && !await mongo.Books.Find(tx, b => b.Id == request.BookId && b.IsActive).AnyAsync(token)) return SupportResult<SupportTicketDetail>.Fail(404, "support_reference_unavailable");
        if (request.LoanId != null && !await mongo.Loans.Find(tx, l => l.Id == request.LoanId && l.UserId == actorId && (request.BookId == null || l.BookId == request.BookId)).AnyAsync(token)) return SupportResult<SupportTicketDetail>.Fail(404, "support_reference_unavailable");
        var now = Now; var since = now.AddHours(-1);
        if (await Tickets.CountDocumentsAsync(tx, t => t.RequesterId == actorId && (t.Status == "open" || t.Status == "in_progress"), cancellationToken: token) >= Options.MaxOpenTickets)
            return SupportResult<SupportTicketDetail>.Fail(429, "support_open_limit", 3600);
        if (await Tickets.CountDocumentsAsync(tx, t => t.RequesterId == actorId && t.CreatedAt > since, cancellationToken: token) >= Options.CreatesPerHour)
            return SupportResult<SupportTicketDetail>.Fail(429, "support_rate_limit", 3600);
        var ticket = new SupportTicket { RequesterId = actorId, Category = request.Category, Subject = request.Subject.Trim(), Description = request.Description.Trim(), BookId = request.BookId, LoanId = request.LoanId, IdempotencyKey = canonicalKey, CreatedAt = now, UpdatedAt = now };
        await Tickets.InsertOneAsync(tx, ticket, cancellationToken: token);
        await History.InsertOneAsync(tx, new() { TicketId = ticket.Id, ActorId = actorId, Action = "created", ToStatus = "open", CreatedAt = now }, cancellationToken: token);
        return new(ToDetail(ticket));
    }, ct);

    private static SupportTicketDetail ToDetail(SupportTicket t) => new(t.Id, t.Category, t.Subject, t.Description, t.BookId, t.Status, t.Version, t.CreatedAt, t.UpdatedAt, t.ResolvedAt, new PagedResult<SupportMessageResponse>([], 1, 20, 0));

    public Task<SupportResult<long>> ReplyAsync(string actorId, string id, AddSupportMessage request, bool staff, CancellationToken ct) =>
        MessageAsync(actorId, id, request, staff, false, ct);
    public Task<SupportResult<long>> AddNoteAsync(string actorId, string id, AddSupportMessage request, CancellationToken ct) =>
        MessageAsync(actorId, id, request, true, true, ct);

    private Task<SupportResult<long>> MessageAsync(string actorId, string id, AddSupportMessage request, bool staff, bool note, CancellationToken ct) =>
        MutateAsync(actorId, id, request.ExpectedVersion, staff, async (tx, actor, ticket, token) => {
            if (!SupportRules.ValidText(request.Text, 4000)) return SupportResult<long>.Fail(400, "support_input_invalid");
            if (ticket.Status is not ("open" or "in_progress")) return SupportResult<long>.Fail(409, "support_state_conflict");
            var since = Now.AddHours(-1);
            var count = await Replies.CountDocumentsAsync(tx, r => r.AuthorId == actor.Id && r.CreatedAt > since, cancellationToken: token) + await Notes.CountDocumentsAsync(tx, r => r.AuthorId == actor.Id && r.CreatedAt > since, cancellationToken: token);
            if (count >= Options.MessagesPerHour) return SupportResult<long>.Fail(429, "support_rate_limit", 3600);
            if (note) await Notes.InsertOneAsync(tx, new() { TicketId = ticket.Id, AuthorId = actor.Id, IsStaff = true, Text = request.Text.Trim(), CreatedAt = Now }, cancellationToken: token);
            else await Replies.InsertOneAsync(tx, new() { TicketId = ticket.Id, AuthorId = actor.Id, IsStaff = staff, Text = request.Text.Trim(), CreatedAt = Now }, cancellationToken: token);
            await RecordAsync(tx, ticket, actor.Id, note ? "note" : "reply", ticket.Status, "", token);
            if (staff && !note) await SupportNotifications.AppendAsync(mongo._database, tx, ticket, ticket.Id + ":reply:" + ticket.Version, Now, token);
            return new(ticket.Version);
        }, ct);

    public Task<SupportResult<long>> ChangeStatusAsync(string actorId, string id, ChangeSupportStatus request, bool staff, CancellationToken ct) =>
        MutateAsync(actorId, id, request.ExpectedVersion, staff, async (tx, actor, ticket, token) => {
            if (!SupportRules.ValidStatus(request.Status) || !SupportRules.ValidText(request.Reason, 4000)) return SupportResult<long>.Fail(400, "support_input_invalid");
            if (!SupportRules.CanTransition(ticket.Status, request.Status, !staff && ticket.RequesterId == actor.Id, staff, ticket.ResolvedAt, Now, Options.ReopenDays)) return SupportResult<long>.Fail(409, "support_state_conflict");
            if (request.Status == "open" && await Tickets.CountDocumentsAsync(tx, t => t.RequesterId == ticket.RequesterId && (t.Status == "open" || t.Status == "in_progress"), cancellationToken: token) >= Options.MaxOpenTickets)
                return SupportResult<long>.Fail(429, "support_open_limit", 3600);
            var previous = ticket.Status; ticket.Status = request.Status;
            if (ticket.Status == "resolved") ticket.ResolvedAt = Now;
            if (ticket.Status == "open") ticket.ResolvedAt = null;
            if (ticket.Status == "closed") ticket.ClosedAt = Now;
            await RecordAsync(tx, ticket, actor.Id, "status", previous, request.Reason.Trim(), token);
            await SupportNotifications.AppendAsync(mongo._database, tx, ticket, ticket.Id + ":status:" + ticket.Version, Now, token);
            return new(ticket.Version);
        }, ct);

    public Task<SupportResult<long>> AssignAsync(string actorId, string id, AssignSupportTicket request, CancellationToken ct) =>
        MutateAsync(actorId, id, request.ExpectedVersion, true, async (tx, actor, ticket, token) => {
            if (ticket.Status == "closed") return SupportResult<long>.Fail(409, "support_state_conflict");
            if (request.AssigneeId != null)
            {
                var assignee = ObjectId.TryParse(request.AssigneeId, out _) ? await mongo.Users.Find(tx, u => u.Id == request.AssigneeId && u.IsActive).FirstOrDefaultAsync(token) : null;
                if (assignee == null || !SupportRules.CanAttend(assignee.Role, ticket.Category)) return SupportResult<long>.Fail(404, "support_assignee_unavailable");
                await UserReferenceGuard.TouchAsync(mongo.Users, tx, assignee.Id, token);
            }
            ticket.AssigneeId = request.AssigneeId;
            await RecordAsync(tx, ticket, actor.Id, "assignment", ticket.Status, "", token);
            return new(ticket.Version);
        }, ct);

    private Task<SupportResult<long>> MutateAsync(string actorId, string id, long version, bool staff, Func<IClientSessionHandle, User, SupportTicket, CancellationToken, Task<SupportResult<long>>> action, CancellationToken ct) =>
        TransactAsync<long>(actorId, async (tx, actor, token) => {
            if (staff && actor.Role is not ("admin" or "librarian")) return SupportResult<long>.Fail(403, "support_staff_required");
            var ticket = ObjectId.TryParse(id, out _) ? await Tickets.Find(tx, t => t.Id == id).FirstOrDefaultAsync(token) : null;
            if (ticket == null || (staff ? !SupportRules.CanAttend(actor.Role, ticket.Category) : ticket.RequesterId != actorId)) return SupportResult<long>.Fail(404, "support_not_found");
            if (version <= 0) return SupportResult<long>.Fail(400, "support_version_required");
            if (ticket.Version != version) return SupportResult<long>.Fail(409, "support_conflict");
            // Existing tickets remain serviceable when their requester is inactive.
            var owner = await mongo.Users.UpdateOneAsync(tx, u => u.Id == ticket.RequesterId, Builders<User>.Update.Inc(u => u.ReferenceVersion, 1), cancellationToken: token);
            if (owner.MatchedCount != 1) return SupportResult<long>.Fail(404, "support_not_found");
            return await action(tx, actor, ticket, token);
        }, ct);

    private async Task RecordAsync(IClientSessionHandle tx, SupportTicket ticket, string actorId, string action, string previous, string reason, CancellationToken ct)
    {
        ticket.Version++; ticket.UpdatedAt = Now;
        await Tickets.ReplaceOneAsync(tx, t => t.Id == ticket.Id, ticket, cancellationToken: ct);
        await History.InsertOneAsync(tx, new() { TicketId = ticket.Id, ActorId = actorId, Action = action, FromStatus = previous, ToStatus = ticket.Status, Reason = reason, CreatedAt = Now }, cancellationToken: ct);
    }
}
