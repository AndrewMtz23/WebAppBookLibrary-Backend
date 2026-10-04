using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Circulation;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Domain.Circulation;
using WebAppBookLibrary.Domain.Loans;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class MongoCirculationStore : ICirculationStore
{
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<Book> _books;
    private readonly IMongoCollection<Loan> _loans;
    private readonly IMongoCollection<User> _users;
    private readonly IMongoCollection<WaitlistEntry> _waitlist;
    private readonly IMongoCollection<PickupReservation> _pickupReservations;
    private readonly IMongoCollection<RenewalRequest> _renewalRequests;
    private readonly IMongoCollection<CirculationHistoryEntry> _history;
    private readonly CirculationOptions _options;
    private readonly bool _notificationsEnabled;
    private readonly TimeProvider _clock;

    public CirculationOptions Options => _options;
    public Task<string> GetModeAsync(CancellationToken ct) => CirculationMode.ReadAsync(_db, _options.Mode, ct);

    public async Task<WebAppBookLibrary.Contracts.Loans.LoanOperationResult> ReserveLegacyAsync(string userId, string bookId, string actor, DateTime now, CancellationToken ct)
    {
        await GetModeAsync(ct);
        using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) => {
            if (await CirculationMode.GuardAsync(_db, tx, token) != "legacy") return new WebAppBookLibrary.Contracts.Loans.LoanOperationResult(false, "physical_pickup_required");
            var book = await _books.Find(tx, b => b.Id == bookId && b.IsActive && b.MediaType == "physical").FirstOrDefaultAsync(token);
            if (book == null) return new WebAppBookLibrary.Contracts.Loans.LoanOperationResult(false, "book_not_found");
            var available = book.AvailableCopies ?? (book.IsAvailable ? 1 : 0);
            if (available <= 0) return new WebAppBookLibrary.Contracts.Loans.LoanOperationResult(false, "book_unavailable");
            if (await _loans.Find(tx, l => l.BookId == bookId && l.UserId == userId && (l.Status == "active" || l.Status == "overdue" || ((l.Status == "" || l.Status == null) && !l.IsReturned))).AnyAsync(token))
                return new WebAppBookLibrary.Contracts.Loans.LoanOperationResult(false, "duplicate_active_reservation");
            if (await _pickupReservations.Find(tx, p => p.BookId == bookId && p.Status == "ready").AnyAsync(token) || await _waitlist.Find(tx, w => w.BookId == bookId && (w.Status == "queued" || w.Status == "offered")).AnyAsync(token))
                return new WebAppBookLibrary.Contracts.Loans.LoanOperationResult(false, "physical_pickup_required");
            await UserReferenceGuard.TouchAsync(_users, tx, userId, token);
            var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = userId, BookId = bookId, MediaType = "physical", Status = "active", ReservedAt = now, LoanDate = now, DueAt = now.AddDays(14), PolicyVersion = "legacy", CreatedBy = actor, ActiveReservationKey = $"{userId}:{bookId}" };
            await _books.UpdateOneAsync(tx, b => b.Id == bookId, Builders<Book>.Update.Set(b => b.AvailableCopies, available - 1).Set(b => b.TotalCopies, book.TotalCopies ?? 1).Set(b => b.IsAvailable, available > 1).Inc(b => b.ReferenceVersion, 1), cancellationToken: token);
            await _loans.InsertOneAsync(tx, loan, cancellationToken: token);
            if (_notificationsEnabled) await NotificationEvents.AppendAsync(_db, tx, loan, "reserved", now, token);
            return new WebAppBookLibrary.Contracts.Loans.LoanOperationResult(true, "", loan);
        }, cancellationToken: ct);
    }

    public MongoCirculationStore(
        MongoDBService mongoDbService,
        IOptions<CirculationOptions> options,
        IOptions<NotificationOptions>? notificationOptions = null, TimeProvider? clock = null)
    {
        _db = mongoDbService._database;
        _books = mongoDbService.Books;
        _loans = mongoDbService.Loans;
        _users = mongoDbService.Users;
        _waitlist = mongoDbService.WaitlistEntries;
        _pickupReservations = mongoDbService.PickupReservations;
        _renewalRequests = mongoDbService.RenewalRequests;
        _history = mongoDbService.CirculationHistory;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
        _notificationsEnabled = notificationOptions?.Value.Enabled ?? true;
    }

    public static async Task CreateIndexesAsync(IMongoDatabase db)
    {
        await db.GetCollection<Loan>("Loans").Indexes.CreateOneAsync(new CreateIndexModel<Loan>(Builders<Loan>.IndexKeys.Ascending(l => l.PickupReservationId), new CreateIndexOptions<Loan> { Name = "ux_loan_pickup", Unique = true, PartialFilterExpression = Builders<Loan>.Filter.Exists(l => l.PickupReservationId) }));
        var waitlist = db.GetCollection<WaitlistEntry>("WaitlistEntries");
        await waitlist.Indexes.CreateOneAsync(new CreateIndexModel<WaitlistEntry>(Builders<WaitlistEntry>.IndexKeys.Ascending(w => w.UserId).Ascending(w => w.IdempotencyKey), new CreateIndexOptions<WaitlistEntry> { Name = "ux_waitlist_request", Unique = true, PartialFilterExpression = Builders<WaitlistEntry>.Filter.Exists(w => w.IdempotencyKey) }));
        await waitlist.Indexes.CreateManyAsync([
            new CreateIndexModel<WaitlistEntry>(
                Builders<WaitlistEntry>.IndexKeys.Ascending(w => w.UserId).Ascending(w => w.BookId),
                new CreateIndexOptions<WaitlistEntry>
                {
                    Name = "ux_waitlist_active_user_book",
                    Unique = true,
                    PartialFilterExpression = Builders<WaitlistEntry>.Filter.In(w => w.Status, WaitlistStatuses.ActiveStatuses)
                }),
            new CreateIndexModel<WaitlistEntry>(
                Builders<WaitlistEntry>.IndexKeys.Ascending(w => w.BookId).Ascending(w => w.Status).Ascending(w => w.CreatedAt).Ascending(w => w.Id),
                new CreateIndexOptions { Name = "ix_waitlist_fifo" }),
            new CreateIndexModel<WaitlistEntry>(
                Builders<WaitlistEntry>.IndexKeys.Ascending(w => w.UserId).Descending(w => w.CreatedAt),
                new CreateIndexOptions { Name = "ix_waitlist_user_created" }),
            new CreateIndexModel<WaitlistEntry>(
                Builders<WaitlistEntry>.IndexKeys.Ascending(w => w.Status).Ascending(w => w.OfferExpiresAt),
                new CreateIndexOptions { Name = "ix_waitlist_status_expires" })
        ]);

        var pickups = db.GetCollection<PickupReservation>("PickupReservations");
        await pickups.Indexes.CreateOneAsync(new CreateIndexModel<PickupReservation>(Builders<PickupReservation>.IndexKeys.Ascending(p => p.UserId).Ascending(p => p.IdempotencyKey), new CreateIndexOptions<PickupReservation> { Name = "ux_pickup_request", Unique = true, PartialFilterExpression = Builders<PickupReservation>.Filter.Exists(p => p.IdempotencyKey) }));
        await pickups.Indexes.CreateManyAsync([
            new CreateIndexModel<PickupReservation>(
                Builders<PickupReservation>.IndexKeys.Ascending(p => p.UserId).Ascending(p => p.BookId),
                new CreateIndexOptions<PickupReservation>
                {
                    Name = "ux_pickup_ready_user_book",
                    Unique = true,
                    PartialFilterExpression = Builders<PickupReservation>.Filter.Eq(p => p.Status, PickupReservationStatuses.Ready)
                }),
            new CreateIndexModel<PickupReservation>(
                Builders<PickupReservation>.IndexKeys.Ascending(p => p.WaitlistEntryId),
                new CreateIndexOptions<PickupReservation>
                {
                    Name = "ux_pickup_waitlist",
                    Unique = true,
                    PartialFilterExpression = Builders<PickupReservation>.Filter.Exists(p => p.WaitlistEntryId)
                }),
            new CreateIndexModel<PickupReservation>(
                Builders<PickupReservation>.IndexKeys.Ascending(p => p.LoanId),
                new CreateIndexOptions<PickupReservation>
                {
                    Name = "ux_pickup_loan",
                    Unique = true,
                    PartialFilterExpression = Builders<PickupReservation>.Filter.Exists(p => p.LoanId)
                }),
            new CreateIndexModel<PickupReservation>(
                Builders<PickupReservation>.IndexKeys.Ascending(p => p.UserId).Descending(p => p.ReadyAt),
                new CreateIndexOptions { Name = "ix_pickup_user_ready" }),
            new CreateIndexModel<PickupReservation>(
                Builders<PickupReservation>.IndexKeys.Ascending(p => p.Status).Ascending(p => p.PickupExpiresAt),
                new CreateIndexOptions { Name = "ix_pickup_status_expires" }),
            new CreateIndexModel<PickupReservation>(
                Builders<PickupReservation>.IndexKeys.Ascending(p => p.Status).Descending(p => p.ReadyAt),
                new CreateIndexOptions { Name = "ix_pickup_status_ready" })
        ]);

        var renewals = db.GetCollection<RenewalRequest>("RenewalRequests");
        await renewals.Indexes.CreateOneAsync(new CreateIndexModel<RenewalRequest>(Builders<RenewalRequest>.IndexKeys.Ascending(r => r.UserId).Ascending(r => r.IdempotencyKey), new CreateIndexOptions<RenewalRequest> { Name = "ux_renewal_request", Unique = true, PartialFilterExpression = Builders<RenewalRequest>.Filter.Exists(r => r.IdempotencyKey) }));
        await renewals.Indexes.CreateManyAsync([
            new CreateIndexModel<RenewalRequest>(
                Builders<RenewalRequest>.IndexKeys.Ascending(r => r.LoanId),
                new CreateIndexOptions<RenewalRequest>
                {
                    Name = "ux_renewal_pending_loan",
                    Unique = true,
                    PartialFilterExpression = Builders<RenewalRequest>.Filter.Eq(r => r.Status, RenewalRequestStatuses.Pending)
                }),
            new CreateIndexModel<RenewalRequest>(
                Builders<RenewalRequest>.IndexKeys.Ascending(r => r.UserId).Descending(r => r.RequestedAt),
                new CreateIndexOptions { Name = "ix_renewal_user_requested" }),
            new CreateIndexModel<RenewalRequest>(
                Builders<RenewalRequest>.IndexKeys.Ascending(r => r.Status).Descending(r => r.RequestedAt),
                new CreateIndexOptions { Name = "ix_renewal_status_requested" })
        ]);

        var history = db.GetCollection<CirculationHistoryEntry>("CirculationHistory");
        await history.Indexes.CreateManyAsync([
            new CreateIndexModel<CirculationHistoryEntry>(
                Builders<CirculationHistoryEntry>.IndexKeys.Ascending(h => h.EntityId).Ascending(h => h.EntityType).Descending(h => h.Timestamp),
                new CreateIndexOptions { Name = "ix_circhistory_entity" }),
            new CreateIndexModel<CirculationHistoryEntry>(
                Builders<CirculationHistoryEntry>.IndexKeys.Ascending(h => h.BookId).Descending(h => h.Timestamp),
                new CreateIndexOptions { Name = "ix_circhistory_book" }),
            new CreateIndexModel<CirculationHistoryEntry>(
                Builders<CirculationHistoryEntry>.IndexKeys.Ascending(h => h.UserId).Descending(h => h.Timestamp),
                new CreateIndexOptions { Name = "ix_circhistory_user" })
        ]);
    }

    #region Waitlist Operations

    public async Task<WaitlistJoinResult> JoinWaitlistAsync(string userId, string bookId, CancellationToken ct, string? idempotencyKey = null)
    {
        await GetModeAsync(ct);

        using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) =>
        {
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                var prior = await _waitlist.Find(tx, p => p.UserId == userId && p.IdempotencyKey == idempotencyKey).FirstOrDefaultAsync(token);
                if (prior != null) return prior.BookId == bookId ? new WaitlistJoinResult(true, null, prior) : new WaitlistJoinResult(false, "idempotency_conflict");
            }
            if (await CirculationMode.GuardAsync(_db, tx, token) != "active") return new WaitlistJoinResult(false, "circulation_paused");
            var user = await _users.Find(tx, u => u.Id == userId && u.IsActive).FirstOrDefaultAsync(token);
            if (user is null) return new WaitlistJoinResult(false, "invalid_user");

            var book = await _books.Find(tx, b => b.Id == bookId && b.IsActive && b.MediaType == MediaTypes.Physical).FirstOrDefaultAsync(token);
            if (book is null) return new WaitlistJoinResult(false, "book_not_found");
            if (!await InventoryConsistentAsync(tx, book, token)) return new WaitlistJoinResult(false, "inventory_reconciliation_required");

            // Guard: active loan check
            var hasActiveLoan = await _loans.Find(tx, l => l.UserId == userId && l.BookId == bookId && (l.Status == LoanStatuses.Active || l.Status == LoanStatuses.Overdue || ((l.Status == "" || l.Status == null) && !l.IsReturned))).AnyAsync(token);
            if (hasActiveLoan) return new WaitlistJoinResult(false, "duplicate_active_reservation");

            // Guard: active pickup check
            var hasActivePickup = await _pickupReservations.Find(tx, p => p.UserId == userId && p.BookId == bookId && p.Status == PickupReservationStatuses.Ready).AnyAsync(token);
            if (hasActivePickup) return new WaitlistJoinResult(false, "duplicate_active_pickup");

            // Guard: active waitlist check
            var hasActiveWaitlist = await _waitlist.Find(tx, w => w.UserId == userId && w.BookId == bookId && (w.Status == WaitlistStatuses.Queued || w.Status == WaitlistStatuses.Offered)).AnyAsync(token);
            if (hasActiveWaitlist) return new WaitlistJoinResult(false, "duplicate_waitlist_entry");

            var now = _clock.GetUtcNow().UtcDateTime;
            await UserReferenceGuard.TouchAsync(_users, tx, userId, token);
            await _books.UpdateOneAsync(tx, b => b.Id == bookId, Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1), cancellationToken: token);

            // Check if queue is empty and units are available for immediate offer
            var queueCount = await _waitlist.CountDocumentsAsync(tx, w => w.BookId == bookId && w.Status == WaitlistStatuses.Queued, cancellationToken: token);
            var availableCopies = book.AvailableCopies ?? 0;

            if (queueCount == 0 && availableCopies > 0)
            {
                // Immediate offer
                var entryId = ObjectId.GenerateNewId().ToString();
                var pickupId = ObjectId.GenerateNewId().ToString();
                var expiresAt = now.AddHours(_options.PickupHours);
                var snapshot = new CirculationPolicySnapshot
                {
                    PickupHours = _options.PickupHours,
                    LoanDays = _options.LoanDays,
                    RenewalDays = _options.RenewalDays,
                    MaxRenewals = _options.MaxRenewals
                };

                var reservation = new PickupReservation
                {
                    Id = pickupId,
                    UserId = userId,
                    BookId = bookId,
                    Status = PickupReservationStatuses.Ready,
                    ReadyAt = now,
                    PickupExpiresAt = expiresAt,
                    WaitlistEntryId = entryId,
                    PolicySnapshot = snapshot,
                    Version = 1
                };
                await _pickupReservations.InsertOneAsync(tx, reservation, cancellationToken: token);

                var entry = new WaitlistEntry
                {
                    IdempotencyKey = idempotencyKey,
                    Id = entryId,
                    UserId = userId,
                    BookId = bookId,
                    Status = WaitlistStatuses.Offered,
                    CreatedAt = now,
                    OfferedAt = now,
                    OfferExpiresAt = expiresAt,
                    PickupReservationId = pickupId,
                    Version = 1
                };
                await _waitlist.InsertOneAsync(tx, entry, cancellationToken: token);

                var newAvailable = availableCopies - 1;
                var newRetained = (book.RetainedCopies ?? 0) + 1;
                await _books.UpdateOneAsync(tx, b => b.Id == bookId,
                    Builders<Book>.Update
                        .Set(b => b.AvailableCopies, newAvailable)
                        .Set(b => b.RetainedCopies, newRetained)
                        .Set(b => b.IsAvailable, newAvailable > 0)
                        .Set(b => b.UpdatedAt, now),
                    cancellationToken: token);

                await RecordHistoryAsync(_history, tx, "waitlist", entryId, bookId, userId, null, WaitlistStatuses.Offered, userId, "user", now, "Immediate offer on waitlist join", token);
                await RecordHistoryAsync(_history, tx, "pickup_reservation", pickupId, bookId, userId, null, PickupReservationStatuses.Ready, userId, "user", now, "Immediate offer reservation created", token);

                if (_notificationsEnabled)
                {
                    var eventKey = $"pickup:{pickupId}:ready:1";
                    await NotificationEvents.AppendEventAsync(
                        _db, tx, userId, eventKey, "pickup_ready",
                        "Tu libro está listo para recoger",
                        $"Tienes 48 horas (hasta {expiresAt:dd/MM/yyyy HH:mm} UTC) para recoger tu ejemplar.",
                        bookId, null, expiresAt, now, token);
                }

                return new WaitlistJoinResult(true, null, entry, 1, true, reservation);
            }
            else
            {
                // Standard queue
                var entry = new WaitlistEntry
                {
                    IdempotencyKey = idempotencyKey,
                    Id = ObjectId.GenerateNewId().ToString(),
                    UserId = userId,
                    BookId = bookId,
                    Status = WaitlistStatuses.Queued,
                    CreatedAt = now,
                    Version = 1
                };
                await _waitlist.InsertOneAsync(tx, entry, cancellationToken: token);
                await RecordHistoryAsync(_history, tx, "waitlist", entry.Id, bookId, userId, null, WaitlistStatuses.Queued, userId, "user", now, "Joined waitlist queue", token);

                var position = (int)queueCount + 1;
                return new WaitlistJoinResult(true, null, entry, position, false, null);
            }
        }, cancellationToken: ct);
    }

    public async Task<bool> LeaveWaitlistAsync(string userId, string waitlistEntryId, string? reason, CancellationToken ct)
    {
        using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) =>
        {
            var entry = await _waitlist.Find(tx, w => w.Id == waitlistEntryId && w.UserId == userId).FirstOrDefaultAsync(token);
            if (entry is null) return false;
            if (entry.Status is not (WaitlistStatuses.Queued or WaitlistStatuses.Offered))
                return true; // Idempotent

            var now = _clock.GetUtcNow().UtcDateTime;
            await _users.UpdateOneAsync(tx, u => u.Id == userId, Builders<User>.Update.Inc(u => u.ReferenceVersion, 1), cancellationToken: token);
            await _books.UpdateOneAsync(tx, b => b.Id == entry.BookId, Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1), cancellationToken: token);

            if (entry.Status == WaitlistStatuses.Offered && !string.IsNullOrWhiteSpace(entry.PickupReservationId))
            {
                await _pickupReservations.UpdateOneAsync(tx,
                    p => p.Id == entry.PickupReservationId && p.Status == PickupReservationStatuses.Ready,
                    Builders<PickupReservation>.Update
                        .Set(p => p.Status, PickupReservationStatuses.Cancelled)
                        .Set(p => p.CancelledAt, now)
                        .Set(p => p.CancelledReason, reason ?? "Waitlist left by user")
                        .Inc(p => p.Version, 1),
                    cancellationToken: token);

                await RecordHistoryAsync(_history, tx, "pickup_reservation", entry.PickupReservationId, entry.BookId, userId,
                    PickupReservationStatuses.Ready, PickupReservationStatuses.Cancelled, userId, "user", now, reason ?? "Waitlist left", token);

                // Reallocate held unit
                await ReallocateOrReleaseUnitAsync(tx, entry.BookId, now, token);
            }

            await _waitlist.UpdateOneAsync(tx,
                w => w.Id == waitlistEntryId,
                Builders<WaitlistEntry>.Update
                    .Set(w => w.Status, WaitlistStatuses.Cancelled)
                    .Set(w => w.ClosedReason, reason ?? "Left by user")
                    .Inc(w => w.Version, 1),
                cancellationToken: token);

            await RecordHistoryAsync(_history, tx, "waitlist", entry.Id, entry.BookId, userId,
                entry.Status, WaitlistStatuses.Cancelled, userId, "user", now, reason ?? "Left waitlist", token);

            return true;
        }, cancellationToken: ct);
    }

    public async Task<MyWaitlistResponse> GetMyWaitlistAsync(string userId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var filter = Builders<WaitlistEntry>.Filter.Eq(w => w.UserId, userId) &
                     Builders<WaitlistEntry>.Filter.In(w => w.Status, WaitlistStatuses.ActiveStatuses);

        var total = await _waitlist.CountDocumentsAsync(filter, cancellationToken: ct);
        var entries = await _waitlist.Find(filter)
            .SortByDescending(w => w.CreatedAt).ThenByDescending(w => w.Id)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var items = new List<WaitlistEntryResponse>(entries.Count);
        foreach (var entry in entries)
        {
            var book = await _books.Find(b => b.Id == entry.BookId).FirstOrDefaultAsync(ct);
            int? queuePos = null;
            if (entry.Status == WaitlistStatuses.Queued)
            {
                queuePos = await GetQueuePositionAsync(entry.BookId, entry.CreatedAt, entry.Id, ct);
            }

            items.Add(new WaitlistEntryResponse(
                entry.Id,
                entry.BookId,
                book?.Title,
                book?.Authors.FirstOrDefault() ?? book?.Author,
                book?.CoverUrl,
                entry.Status,
                entry.CreatedAt,
                queuePos,
                entry.OfferExpiresAt,
                entry.PickupReservationId
            ));
        }

        return new MyWaitlistResponse(items, (int)total);
    }

    public async Task<int> GetQueuePositionAsync(string bookId, DateTime createdAt, string entryId, CancellationToken ct)
    {
        var filter = Builders<WaitlistEntry>.Filter.Eq(w => w.BookId, bookId) &
                     Builders<WaitlistEntry>.Filter.Eq(w => w.Status, WaitlistStatuses.Queued) &
                     (Builders<WaitlistEntry>.Filter.Lt(w => w.CreatedAt, createdAt) |
                      (Builders<WaitlistEntry>.Filter.Eq(w => w.CreatedAt, createdAt) & Builders<WaitlistEntry>.Filter.Lt(w => w.Id, entryId)));

        var aheadCount = await _waitlist.CountDocumentsAsync(filter, cancellationToken: ct);
        return (int)aheadCount + 1;
    }

    #endregion

    #region Pickup Reservation Operations

    public async Task<ReservePickupResult> ReserveForPickupAsync(string userId, string bookId, string? idempotencyKey, CancellationToken ct)
    {
        await GetModeAsync(ct);

        using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) =>
        {

            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                var prior = await _pickupReservations.Find(tx, p => p.UserId == userId && p.IdempotencyKey == idempotencyKey).FirstOrDefaultAsync(token);
                if (prior != null) return prior.BookId == bookId ? new ReservePickupResult(true, null, prior, true) : new ReservePickupResult(false, "idempotency_conflict");
            }
            if (await CirculationMode.GuardAsync(_db, tx, token) != "active") return new ReservePickupResult(false, "circulation_paused");
            var user = await _users.Find(tx, u => u.Id == userId && u.IsActive).FirstOrDefaultAsync(token);
            if (user is null) return new ReservePickupResult(false, "invalid_user");

            var book = await _books.Find(tx, b => b.Id == bookId && b.IsActive && b.MediaType == MediaTypes.Physical).FirstOrDefaultAsync(token);
            if (book is null) return new ReservePickupResult(false, "book_not_found");
            if (!await InventoryConsistentAsync(tx, book, token)) return new ReservePickupResult(false, "inventory_reconciliation_required");

            // Guard: check duplicate active loans
            var hasActiveLoan = await _loans.Find(tx, l => l.UserId == userId && l.BookId == bookId && (l.Status == LoanStatuses.Active || l.Status == LoanStatuses.Overdue || ((l.Status == "" || l.Status == null) && !l.IsReturned))).AnyAsync(token);
            if (hasActiveLoan) return new ReservePickupResult(false, "duplicate_active_reservation");

            // Guard: check duplicate ready pickup
            var existingPickup = await _pickupReservations.Find(tx, p => p.UserId == userId && p.BookId == bookId && p.Status == PickupReservationStatuses.Ready).FirstOrDefaultAsync(token);
            if (existingPickup is not null)
                return new ReservePickupResult(true, null, existingPickup, true);

            // Guard: active waitlist
            var hasActiveWaitlist = await _waitlist.Find(tx, w => w.UserId == userId && w.BookId == bookId && (w.Status == WaitlistStatuses.Queued || w.Status == WaitlistStatuses.Offered)).AnyAsync(token);
            if (hasActiveWaitlist) return new ReservePickupResult(false, "duplicate_waitlist_entry");

            // FIFO protection: if any readers are waiting in waitlist, direct reservation is rejected (must join waitlist)
            var hasQueuedWaitlist = await _waitlist.Find(tx, w => w.BookId == bookId && w.Status == WaitlistStatuses.Queued).AnyAsync(token);
            if (hasQueuedWaitlist) return new ReservePickupResult(false, "waitlist_priority_required");

            var availableCopies = book.AvailableCopies ?? 0;
            if (availableCopies <= 0) return new ReservePickupResult(false, "book_unavailable");

            var now = _clock.GetUtcNow().UtcDateTime;
            await UserReferenceGuard.TouchAsync(_users, tx, userId, token);

            var pickupId = ObjectId.GenerateNewId().ToString();
            var expiresAt = now.AddHours(_options.PickupHours);
            var snapshot = new CirculationPolicySnapshot
            {
                PickupHours = _options.PickupHours,
                LoanDays = _options.LoanDays,
                RenewalDays = _options.RenewalDays,
                MaxRenewals = _options.MaxRenewals
            };

            var reservation = new PickupReservation
            {
                Id = pickupId,
                IdempotencyKey = idempotencyKey,
                UserId = userId,
                BookId = bookId,
                Status = PickupReservationStatuses.Ready,
                ReadyAt = now,
                PickupExpiresAt = expiresAt,
                PolicySnapshot = snapshot,
                Version = 1
            };
            await _pickupReservations.InsertOneAsync(tx, reservation, cancellationToken: token);

            var newAvailable = availableCopies - 1;
            var newRetained = (book.RetainedCopies ?? 0) + 1;
            await _books.UpdateOneAsync(tx, b => b.Id == bookId,
                Builders<Book>.Update
                    .Set(b => b.AvailableCopies, newAvailable)
                    .Set(b => b.RetainedCopies, newRetained)
                    .Set(b => b.IsAvailable, newAvailable > 0)
                    .Set(b => b.UpdatedAt, now)
                    .Inc(b => b.ReferenceVersion, 1),
                cancellationToken: token);

            await RecordHistoryAsync(_history, tx, "pickup_reservation", pickupId, bookId, userId, null, PickupReservationStatuses.Ready, userId, "user", now, "Direct pickup reservation created", token);

            if (_notificationsEnabled)
            {
                var eventKey = $"pickup:{pickupId}:ready:1";
                await NotificationEvents.AppendEventAsync(
                    _db, tx, userId, eventKey, "pickup_ready",
                    "Tu libro está listo para recoger",
                    $"Tienes 48 horas (hasta {expiresAt:dd/MM/yyyy HH:mm} UTC) para recoger tu ejemplar en la biblioteca.",
                    bookId, null, expiresAt, now, token);
            }

            return new ReservePickupResult(true, null, reservation);
        }, cancellationToken: ct);
    }

    public async Task<PickupReservationListPage> GetMyPickupReservationsAsync(string userId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var filter = Builders<PickupReservation>.Filter.Eq(p => p.UserId, userId);
        var total = await _pickupReservations.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _pickupReservations.Find(filter)
            .SortByDescending(p => p.ReadyAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var items = new List<PickupReservationResponse>(rows.Count);
        foreach (var p in rows)
        {
            var book = await _books.Find(b => b.Id == p.BookId).FirstOrDefaultAsync(ct);
            items.Add(new PickupReservationResponse(
                p.Id,
                p.UserId,
                null,
                p.BookId,
                book?.Title,
                book?.Authors.FirstOrDefault() ?? book?.Author,
                book?.CoverUrl,
                p.Status,
                p.ReadyAt,
                p.PickupExpiresAt,
                p.CollectedAt,
                p.CancelledAt,
                p.CancelledReason,
                p.LoanId,
                p.Version
            ));
        }

        return new PickupReservationListPage(items, (int)total, page, pageSize);
    }

    public async Task<PickupReservationListPage> GetStaffPickupReservationsAsync(string? status, string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var builder = Builders<PickupReservation>.Filter;
        var filter = builder.Empty;
        if (!string.IsNullOrWhiteSpace(status))
            filter &= builder.Eq(p => p.Status, status);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var userMatches = await _users.Find(u => u.Username.Contains(search) || (u.Email != null && u.Email.Contains(search)))
                .Project(u => u.Id).ToListAsync(ct);
            var bookMatches = await _books.Find(b => b.Title.Contains(search)).Project(b => b.Id).ToListAsync(ct);
            filter &= (builder.In(p => p.UserId, userMatches) | builder.In(p => p.BookId, bookMatches) | (ObjectId.TryParse(search, out _) ? builder.Eq(p => p.Id, search) : builder.Where(p => false)));
        }

        var total = await _pickupReservations.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _pickupReservations.Find(filter)
            .SortByDescending(p => p.ReadyAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var items = new List<PickupReservationResponse>(rows.Count);
        foreach (var p in rows)
        {
            var book = await _books.Find(b => b.Id == p.BookId).FirstOrDefaultAsync(ct);
            var user = await _users.Find(u => u.Id == p.UserId).FirstOrDefaultAsync(ct);
            items.Add(new PickupReservationResponse(
                p.Id,
                p.UserId,
                user?.DisplayName ?? user?.Username,
                p.BookId,
                book?.Title,
                book?.Authors.FirstOrDefault() ?? book?.Author,
                book?.CoverUrl,
                p.Status,
                p.ReadyAt,
                p.PickupExpiresAt,
                p.CollectedAt,
                p.CancelledAt,
                p.CancelledReason,
                p.LoanId,
                p.Version
            ));
        }

        return new PickupReservationListPage(items, (int)total, page, pageSize);
    }

    public async Task<CollectPickupResult> CollectPickupReservationAsync(string pickupId, string staffUserId, long expectedVersion, CancellationToken ct)
    {
        using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) =>
        {
            var reservation = await _pickupReservations.Find(tx, p => p.Id == pickupId).FirstOrDefaultAsync(token);
            if (reservation is null) return new CollectPickupResult(false, "pickup_not_found");

            // Idempotent retry: if already collected, return existing loan
            if (reservation.Status == PickupReservationStatuses.Collected && !string.IsNullOrWhiteSpace(reservation.LoanId))
            {
                var existingLoan = await _loans.Find(tx, l => l.Id == reservation.LoanId).FirstOrDefaultAsync(token);
                return new CollectPickupResult(true, null, existingLoan, reservation, true);
            }

            if (reservation.Status != PickupReservationStatuses.Ready)
                return new CollectPickupResult(false, "invalid_pickup_status");

            var now = _clock.GetUtcNow().UtcDateTime;
            if (now >= reservation.PickupExpiresAt)
                return new CollectPickupResult(false, "pickup_expired");

            if (expectedVersion > 0 && reservation.Version != expectedVersion)
                return new CollectPickupResult(false, "version_conflict");

            var user = await _users.Find(tx, u => u.Id == reservation.UserId && u.IsActive).FirstOrDefaultAsync(token);
            if (user is null) return new CollectPickupResult(false, "inactive_user");

            var book = await _books.Find(tx, b => b.Id == reservation.BookId && b.IsActive).FirstOrDefaultAsync(token);
            if (book is null) return new CollectPickupResult(false, "inactive_book");
            if (!await InventoryConsistentAsync(tx, book, token)) return new CollectPickupResult(false, "inventory_reconciliation_required");

            var loanId = ObjectId.GenerateNewId().ToString();
            var dueAt = now.AddDays(reservation.PolicySnapshot.LoanDays);

            var loan = new Loan
            {
                Id = loanId,
                BookId = reservation.BookId,
                UserId = reservation.UserId,
                MediaType = MediaTypes.Physical,
                Status = LoanStatuses.Active,
                ReservedAt = reservation.ReadyAt,
                CheckedOutAt = now,
                DueAt = dueAt,
                LoanDate = now,
                PolicyVersion = reservation.PolicySnapshot.PolicyVersion,
                RenewalCount = 0,
                PickupReservationId = reservation.Id,
                ActiveReservationKey = $"{reservation.UserId}:{reservation.BookId}",
                PolicySnapshot = reservation.PolicySnapshot,
                CreatedBy = staffUserId
            };
            await _loans.InsertOneAsync(tx, loan, cancellationToken: token);

            // Update reservation
            await _pickupReservations.UpdateOneAsync(tx,
                p => p.Id == reservation.Id && p.Version == reservation.Version,
                Builders<PickupReservation>.Update
                    .Set(p => p.Status, PickupReservationStatuses.Collected)
                    .Set(p => p.CollectedAt, now)
                    .Set(p => p.LoanId, loanId)
                    .Inc(p => p.Version, 1),
                cancellationToken: token);

            // Fulfill origin waitlist entry if present
            if (!string.IsNullOrWhiteSpace(reservation.WaitlistEntryId))
            {
                await _waitlist.UpdateOneAsync(tx,
                    w => w.Id == reservation.WaitlistEntryId,
                    Builders<WaitlistEntry>.Update
                        .Set(w => w.Status, WaitlistStatuses.Fulfilled)
                        .Inc(w => w.Version, 1),
                    cancellationToken: token);

                await RecordHistoryAsync(_history, tx, "waitlist", reservation.WaitlistEntryId, reservation.BookId, reservation.UserId,
                    WaitlistStatuses.Offered, WaitlistStatuses.Fulfilled, staffUserId, "staff", now, "Fulfilled upon physical pickup", token);
            }

            // Decrement retained copies
            var retained = (book.RetainedCopies ?? 0) - 1;
            await _books.UpdateOneAsync(tx, b => b.Id == reservation.BookId,
                Builders<Book>.Update
                    .Set(b => b.RetainedCopies, retained)
                    .Set(b => b.UpdatedAt, now)
                    .Inc(b => b.ReferenceVersion, 1),
                cancellationToken: token);

            await UserReferenceGuard.TouchAsync(_users, tx, reservation.UserId, token);

            await RecordHistoryAsync(_history, tx, "pickup_reservation", reservation.Id, reservation.BookId, reservation.UserId,
                PickupReservationStatuses.Ready, PickupReservationStatuses.Collected, staffUserId, "staff", now, "Confirmed physical pickup and created loan", token);
            await RecordHistoryAsync(_history, tx, "loan", loanId, reservation.BookId, reservation.UserId,
                null, LoanStatuses.Active, staffUserId, "staff", now, "Physical loan started from pickup", token);

            if (_notificationsEnabled)
            {
                var eventKey = $"loan:{loanId}:collected:1";
                await NotificationEvents.AppendEventAsync(
                    _db, tx, reservation.UserId, eventKey, "loan_collected",
                    "Préstamo entregado",
                    $"Se confirmó la entrega de '{book.Title}'. Fecha límite de devolución: {dueAt:dd/MM/yyyy}.",
                    reservation.BookId, loanId, dueAt, now, token);
            }

            return new CollectPickupResult(true, null, loan, reservation);
        }, cancellationToken: ct);
    }

    public async Task<bool> CancelPickupReservationAsync(string pickupId, string actorId, bool isStaff, string? reason, long expectedVersion, CancellationToken ct)
    {
        using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) =>
        {
            var reservation = await _pickupReservations.Find(tx, p => p.Id == pickupId).FirstOrDefaultAsync(token);
            if (reservation is null) return false;

            if (reservation.Status is PickupReservationStatuses.Cancelled or PickupReservationStatuses.Expired)
                return true; // Idempotent

            if (reservation.Status != PickupReservationStatuses.Ready)
                return false;

            if (!isStaff && reservation.UserId != actorId)
                return false; // Forbidden

            if (expectedVersion > 0 && reservation.Version != expectedVersion)
                return false; // Version conflict

            var now = _clock.GetUtcNow().UtcDateTime;

            await _pickupReservations.UpdateOneAsync(tx,
                p => p.Id == pickupId && p.Version == reservation.Version,
                Builders<PickupReservation>.Update
                    .Set(p => p.Status, PickupReservationStatuses.Cancelled)
                    .Set(p => p.CancelledAt, now)
                    .Set(p => p.CancelledReason, reason ?? "Cancelled")
                    .Inc(p => p.Version, 1),
                cancellationToken: token);

            if (!string.IsNullOrWhiteSpace(reservation.WaitlistEntryId))
            {
                await _waitlist.UpdateOneAsync(tx,
                    w => w.Id == reservation.WaitlistEntryId,
                    Builders<WaitlistEntry>.Update
                        .Set(w => w.Status, WaitlistStatuses.Cancelled)
                        .Set(w => w.ClosedReason, reason ?? "Pickup reservation cancelled")
                        .Inc(w => w.Version, 1),
                    cancellationToken: token);

                await RecordHistoryAsync(_history, tx, "waitlist", reservation.WaitlistEntryId, reservation.BookId, reservation.UserId,
                    WaitlistStatuses.Offered, WaitlistStatuses.Cancelled, actorId, isStaff ? "staff" : "user", now, reason ?? "Pickup cancelled", token);
            }

            await RecordHistoryAsync(_history, tx, "pickup_reservation", pickupId, reservation.BookId, reservation.UserId,
                PickupReservationStatuses.Ready, PickupReservationStatuses.Cancelled, actorId, isStaff ? "staff" : "user", now, reason ?? "Cancelled", token);

            // Reallocate the physical copy
            await ReallocateOrReleaseUnitAsync(tx, reservation.BookId, now, token);

            if (_notificationsEnabled)
            {
                var eventKey = $"pickup:{pickupId}:cancelled:{reservation.Version + 1}";
                await NotificationEvents.AppendEventAsync(
                    _db, tx, reservation.UserId, eventKey, "pickup_cancelled",
                    "Reserva de recogida cancelada",
                    "Tu reserva para recoger el libro ha sido cancelada.",
                    reservation.BookId, null, null, now, token);
            }

            return true;
        }, cancellationToken: ct);
    }

    #endregion

    #region Renewal Operations

    public async Task<RenewalRequestResult> RequestRenewalAsync(string loanId, string userId, string? reason, CancellationToken ct, string? idempotencyKey = null)
    {
        await GetModeAsync(ct);

        using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) =>
        {
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                var prior = await _renewalRequests.Find(tx, p => p.UserId == userId && p.IdempotencyKey == idempotencyKey).FirstOrDefaultAsync(token);
                if (prior != null) return prior.LoanId == loanId && prior.Reason == reason?.Trim() ? new RenewalRequestResult(true, null, prior) : new RenewalRequestResult(false, "idempotency_conflict");
            }
            if (await CirculationMode.GuardAsync(_db, tx, token) != "active") return new RenewalRequestResult(false, "circulation_paused");
            var loan = await _loans.Find(tx, l => l.Id == loanId && l.UserId == userId).FirstOrDefaultAsync(token);
            if (loan is null) return new RenewalRequestResult(false, "loan_not_found");

            if (loan.MediaType != MediaTypes.Physical || loan.Status != LoanStatuses.Active)
                return new RenewalRequestResult(false, "loan_not_renewable");

            // Legacy loans cannot be renewed under the new policy
            if (loan.PolicyVersion != "circulation-v1")
                return new RenewalRequestResult(false, "legacy_loan_not_renewable");

            var now = _clock.GetUtcNow().UtcDateTime;
            if (loan.DueAt is null || now >= loan.DueAt.Value)
                return new RenewalRequestResult(false, "loan_overdue");

            var maxRenewals = loan.PolicySnapshot?.MaxRenewals ?? _options.MaxRenewals;
            if (loan.RenewalCount >= maxRenewals)
                return new RenewalRequestResult(false, "renewal_limit_exceeded");

            // Check no pending renewal request already exists
            var hasPending = await _renewalRequests.Find(tx, r => r.LoanId == loanId && r.Status == RenewalRequestStatuses.Pending).AnyAsync(token);
            if (hasPending) return new RenewalRequestResult(false, "duplicate_renewal_request");

            // Check no waitlist entries exist for this book!
            var hasWaitlist = await _waitlist.Find(tx, w => w.BookId == loan.BookId && w.Status == WaitlistStatuses.Queued).AnyAsync(token);
            if (hasWaitlist) return new RenewalRequestResult(false, "waitlist_present");

            if (!await _users.Find(tx, u => u.Id == userId && u.IsActive).AnyAsync(token) || !await _books.Find(tx, b => b.Id == loan.BookId && b.IsActive).AnyAsync(token)) return new RenewalRequestResult(false, "renewal_no_longer_eligible");
            await UserReferenceGuard.TouchAsync(_users, tx, userId, token);
            await _books.UpdateOneAsync(tx, b => b.Id == loan.BookId, Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1), cancellationToken: token);
            var renewalDays = loan.PolicySnapshot?.RenewalDays ?? _options.RenewalDays;
            var requestedDueAt = loan.DueAt.Value.AddDays(renewalDays);

            var request = new RenewalRequest
            {
                IdempotencyKey = idempotencyKey,
                Id = ObjectId.GenerateNewId().ToString(),
                LoanId = loanId,
                UserId = userId,
                BookId = loan.BookId,
                Status = RenewalRequestStatuses.Pending,
                RequestedAt = now,
                OriginalDueAt = loan.DueAt.Value,
                OriginalLoanVersion = loan.NotificationVersion,
                RequestedDueAt = requestedDueAt,
                Reason = reason?.Trim(),
                Version = 1
            };
            await _renewalRequests.InsertOneAsync(tx, request, cancellationToken: token);

            await RecordHistoryAsync(_history, tx, "renewal_request", request.Id, loan.BookId, userId,
                null, RenewalRequestStatuses.Pending, userId, "user", now, reason, token);

            if (_notificationsEnabled)
            {
                var eventKey = $"renewal:{request.Id}:pending:1";
                await NotificationEvents.AppendEventAsync(
                    _db, tx, userId, eventKey, "renewal_requested",
                    "Solicitud de renovación enviada",
                    $"Tu solicitud de ampliación de plazo para el libro se encuentra en revisión.",
                    loan.BookId, loanId, requestedDueAt, now, token);
            }

            return new RenewalRequestResult(true, null, request);
        }, cancellationToken: ct);
    }

    public async Task<RenewalRequestListPage> GetMyRenewalRequestsAsync(string userId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var filter = Builders<RenewalRequest>.Filter.Eq(r => r.UserId, userId);
        var total = await _renewalRequests.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _renewalRequests.Find(filter)
            .SortByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var items = new List<RenewalRequestResponse>(rows.Count);
        foreach (var r in rows)
        {
            var book = await _books.Find(b => b.Id == r.BookId).FirstOrDefaultAsync(ct);
            items.Add(new RenewalRequestResponse(
                r.Id,
                r.LoanId,
                r.UserId,
                null,
                r.BookId,
                book?.Title,
                book?.Authors.FirstOrDefault() ?? book?.Author,
                book?.CoverUrl,
                r.Status,
                r.RequestedAt,
                r.OriginalDueAt,
                r.RequestedDueAt,
                r.Reason,
                r.DecidedAt,
                r.DecidedByUserId,
                r.DecisionReason,
                r.Version
            ));
        }

        return new RenewalRequestListPage(items, (int)total, page, pageSize);
    }

    public async Task<RenewalRequestListPage> GetStaffRenewalRequestsAsync(string? status, string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var builder = Builders<RenewalRequest>.Filter;
        var filter = builder.Empty;
        if (!string.IsNullOrWhiteSpace(status))
            filter &= builder.Eq(r => r.Status, status);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var userMatches = await _users.Find(u => u.Username.Contains(search) || (u.Email != null && u.Email.Contains(search)))
                .Project(u => u.Id).ToListAsync(ct);
            var bookMatches = await _books.Find(b => b.Title.Contains(search)).Project(b => b.Id).ToListAsync(ct);
            filter &= (builder.In(r => r.UserId, userMatches) | builder.In(r => r.BookId, bookMatches) | (ObjectId.TryParse(search, out _) ? builder.Eq(r => r.Id, search) : builder.Where(r => false)));
        }

        var total = await _renewalRequests.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _renewalRequests.Find(filter)
            .SortByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var items = new List<RenewalRequestResponse>(rows.Count);
        foreach (var r in rows)
        {
            var book = await _books.Find(b => b.Id == r.BookId).FirstOrDefaultAsync(ct);
            var user = await _users.Find(u => u.Id == r.UserId).FirstOrDefaultAsync(ct);
            items.Add(new RenewalRequestResponse(
                r.Id,
                r.LoanId,
                r.UserId,
                user?.DisplayName ?? user?.Username,
                r.BookId,
                book?.Title,
                book?.Authors.FirstOrDefault() ?? book?.Author,
                book?.CoverUrl,
                r.Status,
                r.RequestedAt,
                r.OriginalDueAt,
                r.RequestedDueAt,
                r.Reason,
                r.DecidedAt,
                r.DecidedByUserId,
                r.DecisionReason,
                r.Version
            ));
        }

        return new RenewalRequestListPage(items, (int)total, page, pageSize);
    }

    public async Task<RenewalDecisionResult> DecideRenewalAsync(string renewalRequestId, string staffUserId, bool approve, string? decisionReason, long expectedVersion, CancellationToken ct)
    {
        using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) =>
        {
            var request = await _renewalRequests.Find(tx, r => r.Id == renewalRequestId).FirstOrDefaultAsync(token);
            if (request is null) return new RenewalDecisionResult(false, "renewal_request_not_found");

            if (request.Status != RenewalRequestStatuses.Pending)
                return new RenewalDecisionResult(false, "renewal_not_pending");

            if (expectedVersion > 0 && request.Version != expectedVersion)
                return new RenewalDecisionResult(false, "version_conflict");

            var loan = await _loans.Find(tx, l => l.Id == request.LoanId).FirstOrDefaultAsync(token);
            if (loan is null) return new RenewalDecisionResult(false, "loan_not_found");

            if (loan.Status != LoanStatuses.Active)
                return new RenewalDecisionResult(false, "loan_not_active");

            var now = _clock.GetUtcNow().UtcDateTime;
            if (approve)
            {
                if (await CirculationMode.GuardAsync(_db, tx, token) != CirculationModes.Active || loan.PolicyVersion != "circulation-v1" || loan.NotificationVersion != request.OriginalLoanVersion || loan.DueAt != request.OriginalDueAt || loan.RenewalCount >= (loan.PolicySnapshot?.MaxRenewals ?? _options.MaxRenewals))
                    return new RenewalDecisionResult(false, "renewal_no_longer_eligible");
                if (!await _users.Find(tx, u => u.Id == loan.UserId && u.IsActive).AnyAsync(token) || !await _books.Find(tx, b => b.Id == loan.BookId && b.IsActive).AnyAsync(token))
                    return new RenewalDecisionResult(false, "renewal_no_longer_eligible");
                await UserReferenceGuard.TouchAsync(_users, tx, loan.UserId, token);
                await _books.UpdateOneAsync(tx, b => b.Id == loan.BookId, Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1), cancellationToken: token);
                // Re-verify waitlist priority: if someone queued up in the meantime, cannot approve!
                var hasWaitlist = await _waitlist.Find(tx, w => w.BookId == loan.BookId && w.Status == WaitlistStatuses.Queued).AnyAsync(token);
                if (hasWaitlist) return new RenewalDecisionResult(false, "waitlist_present");

                // Re-verify not overdue
                if (loan.DueAt is not null && now >= loan.DueAt.Value)
                    return new RenewalDecisionResult(false, "loan_overdue");

                // Update loan with extended due date and increment notification version
                await _loans.UpdateOneAsync(tx, l => l.Id == loan.Id,
                    Builders<Loan>.Update
                        .Set(l => l.DueAt, request.RequestedDueAt)
                        .Inc(l => l.RenewalCount, 1)
                        .Inc(l => l.NotificationVersion, 1),
                    cancellationToken: token);

                loan.DueAt = request.RequestedDueAt;
                loan.RenewalCount += 1;
                loan.NotificationVersion += 1;

                // Update renewal request
                await _renewalRequests.UpdateOneAsync(tx, r => r.Id == renewalRequestId && r.Version == request.Version,
                    Builders<RenewalRequest>.Update
                        .Set(r => r.Status, RenewalRequestStatuses.Approved)
                        .Set(r => r.DecidedAt, now)
                        .Set(r => r.DecidedByUserId, staffUserId)
                        .Set(r => r.DecisionReason, decisionReason)
                        .Inc(r => r.Version, 1),
                    cancellationToken: token);

                request.Status = RenewalRequestStatuses.Approved;
                request.DecidedAt = now;
                request.DecidedByUserId = staffUserId;
                request.DecisionReason = decisionReason;
                request.Version += 1;

                await RecordHistoryAsync(_history, tx, "renewal_request", renewalRequestId, loan.BookId, loan.UserId,
                    RenewalRequestStatuses.Pending, RenewalRequestStatuses.Approved, staffUserId, "staff", now, decisionReason, token);
                await RecordHistoryAsync(_history, tx, "loan", loan.Id, loan.BookId, loan.UserId,
                    LoanStatuses.Active, LoanStatuses.Active, staffUserId, "staff", now, $"Renewal approved: due date extended to {request.RequestedDueAt:yyyy-MM-dd}", token);

                if (_notificationsEnabled)
                {
                    var eventKey = $"renewal:{request.Id}:approved:{request.Version}";
                    await NotificationEvents.AppendEventAsync(
                        _db, tx, loan.UserId, eventKey, "renewal_approved",
                        "Renovación aprobada",
                        $"Tu solicitud de ampliación fue aprobada. La nueva fecha de devolución es {request.RequestedDueAt:dd/MM/yyyy}.",
                        loan.BookId, loan.Id, request.RequestedDueAt, now, token);
                }

                return new RenewalDecisionResult(true, null, request, loan);
            }
            else
            {
                // Reject
                await _renewalRequests.UpdateOneAsync(tx, r => r.Id == renewalRequestId && r.Version == request.Version,
                    Builders<RenewalRequest>.Update
                        .Set(r => r.Status, RenewalRequestStatuses.Rejected)
                        .Set(r => r.DecidedAt, now)
                        .Set(r => r.DecidedByUserId, staffUserId)
                        .Set(r => r.DecisionReason, decisionReason)
                        .Inc(r => r.Version, 1),
                    cancellationToken: token);

                request.Status = RenewalRequestStatuses.Rejected;
                request.DecidedAt = now;
                request.DecidedByUserId = staffUserId;
                request.DecisionReason = decisionReason;
                request.Version += 1;

                await RecordHistoryAsync(_history, tx, "renewal_request", renewalRequestId, loan.BookId, loan.UserId,
                    RenewalRequestStatuses.Pending, RenewalRequestStatuses.Rejected, staffUserId, "staff", now, decisionReason, token);

                if (_notificationsEnabled)
                {
                    var eventKey = $"renewal:{request.Id}:rejected:{request.Version}";
                    await NotificationEvents.AppendEventAsync(
                        _db, tx, loan.UserId, eventKey, "renewal_rejected",
                        "Renovación no aprobada",
                        $"Tu solicitud de ampliación de plazo no fue aprobada: {decisionReason ?? "Plazo máximo alcanzado o ejemplar solicitado."}",
                        loan.BookId, loan.Id, loan.DueAt, now, token);
                }

                return new RenewalDecisionResult(true, null, request, loan);
            }
        }, cancellationToken: ct);
    }

    #endregion

    #region Returns and Expirations

    public async Task<bool> ReturnPhysicalLoanAsync(string loanId, string staffUserId, CancellationToken ct, string nextStatus = "returned")
    {
        using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (tx, token) =>
        {
            var loan = await _loans.Find(tx, l => l.Id == loanId).FirstOrDefaultAsync(token);
            if (loan is null) return false;

            if (loan.MediaType == MediaTypes.Digital || nextStatus is not ("returned" or "cancelled") || (loan.PolicyVersion == "circulation-v1" && nextStatus == "cancelled")) return false;
            if (loan.Status == nextStatus)
                return true; // Idempotent

            if (WebAppBookLibrary.Contracts.Loans.LoanResponse.From(loan, _clock.GetUtcNow().UtcDateTime).Status is not (LoanStatuses.Active or LoanStatuses.Overdue))
                return false;

            var now = _clock.GetUtcNow().UtcDateTime;

            await _loans.UpdateOneAsync(tx, l => l.Id == loanId,
                Builders<Loan>.Update
                    .Set(l => l.Status, nextStatus)
                    .Set(l => l.ReturnedAt, nextStatus == "returned" ? now : null)
                    .Set(l => l.CancelledAt, nextStatus == "cancelled" ? now : null)
                    .Set(l => l.ReturnDate, nextStatus == "returned" ? now : null)
                    .Set(l => l.IsReturned, nextStatus == "returned")
                    .Set(l => l.ActiveReservationKey, null),
                cancellationToken: token);

            // Preserve each decision in the same transaction as the return.
            var pending = await _renewalRequests.Find(tx, r => r.LoanId == loanId && r.Status == RenewalRequestStatuses.Pending).ToListAsync(token);
            foreach (var request in pending)
                await RecordHistoryAsync(_history, tx, "renewal_request", request.Id, loan.BookId, loan.UserId, "pending", "cancelled", staffUserId, "staff", now, "Loan closed", token);
            await _renewalRequests.UpdateManyAsync(tx,
                r => r.LoanId == loanId && r.Status == RenewalRequestStatuses.Pending,
                Builders<RenewalRequest>.Update
                    .Set(r => r.Status, RenewalRequestStatuses.Cancelled)
                    .Set(r => r.DecisionReason, "Loan closed")
                    .Set(r => r.DecidedByUserId, staffUserId)
                    .Set(r => r.DecidedAt, now)
                    .Inc(r => r.Version, 1),
                cancellationToken: token);

            await _users.UpdateOneAsync(tx, u => u.Id == loan.UserId, Builders<User>.Update.Inc(u => u.ReferenceVersion, 1), cancellationToken: token);

            await RecordHistoryAsync(_history, tx, "loan", loanId, loan.BookId, loan.UserId,
                loan.Status, nextStatus, staffUserId, "staff", now,
                nextStatus == "cancelled" ? "Cancelled legacy reservation" : "Confirmed physical return", token);

            // Reallocate or release unit
            await ReallocateOrReleaseUnitAsync(tx, loan.BookId, now, token, fromRetention: false);

            if (_notificationsEnabled)
            {
                var eventKey = $"{loanId}:{nextStatus}";
                await NotificationEvents.AppendEventAsync(
                    _db, tx, loan.UserId, eventKey, nextStatus,
                    nextStatus == "cancelled" ? "Reserva cancelada" : "Devolución confirmada",
                    nextStatus == "cancelled" ? "Tu reserva fue cancelada." : "Registramos la devolución de tu libro.",
                    loan.BookId, loanId, null, now, token);
            }

            return true;
        }, cancellationToken: ct);
    }

    public async Task PromoteAvailableAsync(CancellationToken ct)
    {
        if (await GetModeAsync(ct) != CirculationModes.Active) return;
        var state = _db.GetCollection<BsonDocument>("CirculationWorkerState");
        var saved = await state.Find(new BsonDocument("_id", "promotion")).FirstOrDefaultAsync(ct);
        var cursor = saved?.GetValue("Cursor", "").AsString ?? "";
        var filter = Builders<WaitlistEntry>.Filter.Eq(w => w.Status, WaitlistStatuses.Queued);
        if (cursor.Length > 0) filter &= Builders<WaitlistEntry>.Filter.Gt(w => w.Id, cursor);
        var rows = await _waitlist.Find(filter).SortBy(w => w.Id).Limit(100).ToListAsync(ct);
        foreach (var id in rows.Select(w => w.BookId).Distinct())
        {
            using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
            await session.WithTransactionAsync(async (tx, token) => {
                var book = await _books.Find(tx, b => b.Id == id && b.IsActive && b.AvailableCopies > 0).FirstOrDefaultAsync(token);
                if (book == null || !await _waitlist.Find(tx, w => w.BookId == id && w.Status == WaitlistStatuses.Queued).AnyAsync(token)) return false;
                await _books.UpdateOneAsync(tx, b => b.Id == id, Builders<Book>.Update.Inc(b => b.AvailableCopies, -1).Set(b => b.IsAvailable, book.AvailableCopies > 1).Inc(b => b.ReferenceVersion, 1), cancellationToken: token);
                await ReallocateOrReleaseUnitAsync(tx, id, _clock.GetUtcNow().UtcDateTime, token, fromRetention: false);
                return true;
            }, cancellationToken: ct);
        }
        await state.UpdateOneAsync(new BsonDocument("_id", "promotion"), new BsonDocument("$set", new BsonDocument("Cursor", rows.Count == 100 ? rows[^1].Id : "")), new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<int> ProcessExpiredPickupReservationsAsync(DateTime now, CancellationToken ct)
    {
        var expiredList = await _pickupReservations.Find(p => p.Status == PickupReservationStatuses.Ready && p.PickupExpiresAt <= now)
            .SortBy(p => p.PickupExpiresAt).ThenBy(p => p.Id)
            .Limit(50)
            .ToListAsync(ct);

        int count = 0;
        foreach (var reservation in expiredList)
        {
            try
            {
                using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
                var processed = await session.WithTransactionAsync(async (tx, token) =>
                {
                    // Re-fetch under transaction to guarantee atomic winner
                    var current = await _pickupReservations.Find(tx, p => p.Id == reservation.Id && p.Status == PickupReservationStatuses.Ready && p.PickupExpiresAt <= now).FirstOrDefaultAsync(token);
                    if (current is null) return false;

                    await _pickupReservations.UpdateOneAsync(tx, p => p.Id == current.Id,
                        Builders<PickupReservation>.Update
                            .Set(p => p.Status, PickupReservationStatuses.Expired)
                            .Set(p => p.CancelledAt, now)
                            .Set(p => p.CancelledReason, "Pickup window expired")
                            .Inc(p => p.Version, 1),
                        cancellationToken: token);

                    if (!string.IsNullOrWhiteSpace(current.WaitlistEntryId))
                    {
                        await _waitlist.UpdateOneAsync(tx, w => w.Id == current.WaitlistEntryId,
                            Builders<WaitlistEntry>.Update
                                .Set(w => w.Status, WaitlistStatuses.Expired)
                                .Set(w => w.ClosedReason, "Offer expired")
                                .Inc(w => w.Version, 1),
                            cancellationToken: token);

                        await RecordHistoryAsync(_history, tx, "waitlist", current.WaitlistEntryId, current.BookId, current.UserId,
                            WaitlistStatuses.Offered, WaitlistStatuses.Expired, null, "worker", now, "Offer expired", token);
                    }

                    await RecordHistoryAsync(_history, tx, "pickup_reservation", current.Id, current.BookId, current.UserId,
                        PickupReservationStatuses.Ready, PickupReservationStatuses.Expired, null, "worker", now, "Pickup window expired", token);

                    // Reallocate physical unit to next in waitlist or release
                    await ReallocateOrReleaseUnitAsync(tx, current.BookId, now, token);

                    if (_notificationsEnabled)
                    {
                        var eventKey = $"pickup:{current.Id}:expired:{current.Version + 1}";
                        await NotificationEvents.AppendEventAsync(
                            _db, tx, current.UserId, eventKey, "pickup_expired",
                            "Reserva de recogida expirada",
                            "Tu plazo de 48 horas para recoger el ejemplar ha terminado y la unidad fue reasignada.",
                            current.BookId, null, null, now, token);
                    }

                    return true;
                }, cancellationToken: ct);

                if (processed) count++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw; }
        }

        return count;
    }

    #endregion

    #region Helper Methods

    private async Task<bool> InventoryConsistentAsync(IClientSessionHandle tx, Book book, CancellationToken ct)
    {
        if (book.TotalCopies is null || book.AvailableCopies is null || book.AvailableCopies < 0 || book.RetainedCopies < 0) return false;
        var retained = await _pickupReservations.CountDocumentsAsync(tx, p => p.BookId == book.Id && p.Status == "ready", cancellationToken: ct);
        var loaned = await _loans.CountDocumentsAsync(tx, l => l.BookId == book.Id && (l.Status == "active" || l.Status == "overdue" || ((l.Status == "" || l.Status == null) && !l.IsReturned)), cancellationToken: ct);
        return retained == (book.RetainedCopies ?? 0) && book.AvailableCopies + retained + loaned == book.TotalCopies;
    }

    private async Task<bool> ReallocateOrReleaseUnitAsync(IClientSessionHandle tx, string bookId, DateTime now, CancellationToken ct, bool fromRetention = true)
    {
        var book = await _books.Find(tx, b => b.Id == bookId).FirstOrDefaultAsync(ct);
        if (book is null) return false;

        // Find next eligible queued waitlist entry
        var nextEntry = await _waitlist.Find(tx, w => w.BookId == bookId && w.Status == WaitlistStatuses.Queued)
            .SortBy(w => w.CreatedAt).ThenBy(w => w.Id)
            .FirstOrDefaultAsync(ct);

        var skipped = 0;
        while (nextEntry is not null && skipped < 20 && !await _users.Find(tx, u => u.Id == nextEntry.UserId && u.IsActive).AnyAsync(ct))
        {
            await _waitlist.UpdateOneAsync(tx, w => w.Id == nextEntry.Id, Builders<WaitlistEntry>.Update.Set(w => w.Status, WaitlistStatuses.Cancelled).Set(w => w.ClosedReason, "Inactive account").Inc(w => w.Version, 1), cancellationToken: ct);
            await RecordHistoryAsync(_history, tx, "waitlist", nextEntry.Id, bookId, nextEntry.UserId, "queued", "cancelled", null, "worker", now, "Inactive account", ct);
            skipped++;
            nextEntry = await _waitlist.Find(tx, w => w.BookId == bookId && w.Status == WaitlistStatuses.Queued).SortBy(w => w.CreatedAt).ThenBy(w => w.Id).FirstOrDefaultAsync(ct);
        }
        if (skipped < 20 && nextEntry is not null && book.IsActive && await CirculationMode.GuardAsync(_db, tx, ct) == CirculationModes.Active)
        {
            var pickupId = ObjectId.GenerateNewId().ToString();
            var expiresAt = now.AddHours(_options.PickupHours);
            var snapshot = new CirculationPolicySnapshot
            {
                PickupHours = _options.PickupHours,
                LoanDays = _options.LoanDays,
                RenewalDays = _options.RenewalDays,
                MaxRenewals = _options.MaxRenewals
            };

            var reservation = new PickupReservation
            {
                Id = pickupId,
                UserId = nextEntry.UserId,
                BookId = bookId,
                Status = PickupReservationStatuses.Ready,
                ReadyAt = now,
                PickupExpiresAt = expiresAt,
                WaitlistEntryId = nextEntry.Id,
                PolicySnapshot = snapshot,
                Version = 1
            };
            await _pickupReservations.InsertOneAsync(tx, reservation, cancellationToken: ct);

            await _waitlist.UpdateOneAsync(tx,
                w => w.Id == nextEntry.Id && w.Status == WaitlistStatuses.Queued,
                Builders<WaitlistEntry>.Update
                    .Set(w => w.Status, WaitlistStatuses.Offered)
                    .Set(w => w.OfferedAt, now)
                    .Set(w => w.OfferExpiresAt, expiresAt)
                    .Set(w => w.PickupReservationId, pickupId)
                    .Inc(w => w.Version, 1),
                cancellationToken: ct);

            await RecordHistoryAsync(_history, tx, "waitlist", nextEntry.Id, bookId, nextEntry.UserId,
                WaitlistStatuses.Queued, WaitlistStatuses.Offered, null, "system", now, "Auto-offered by waitlist queue", ct);
            await RecordHistoryAsync(_history, tx, "pickup_reservation", pickupId, bookId, nextEntry.UserId,
                null, PickupReservationStatuses.Ready, null, "system", now, "Created from waitlist offer", ct);

            if (_notificationsEnabled)
            {
                var eventKey = $"pickup:{pickupId}:ready:1";
                await NotificationEvents.AppendEventAsync(
                    _db, tx, nextEntry.UserId, eventKey, "pickup_ready",
                    "Tu libro está listo para recoger",
                    $"Tienes 48 horas (hasta {expiresAt:dd/MM/yyyy HH:mm} UTC) para recoger tu ejemplar en la biblioteca.",
                    bookId, null, expiresAt, now, ct);
            }

            await UserReferenceGuard.TouchAsync(_users, tx, nextEntry.UserId, ct);
            await _books.UpdateOneAsync(tx, b => b.Id == bookId, Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1).Set(b => b.RetainedCopies, (book.RetainedCopies ?? 0) + (fromRetention ? 0 : 1)), cancellationToken: ct);
            return true;
        }
        else
        {
            // No one waiting: release unit back to available
            var retained = (book.RetainedCopies ?? 0) - (fromRetention ? 1 : 0);
            var available = (book.AvailableCopies ?? 0) + 1;
            if (retained < 0 || available + retained > (book.TotalCopies ?? 1)) throw new InvalidOperationException("Inventory reconciliation required");

            await _books.UpdateOneAsync(tx, b => b.Id == bookId,
                Builders<Book>.Update
                    .Set(b => b.AvailableCopies, available)
                    .Set(b => b.RetainedCopies, retained)
                    .Set(b => b.IsAvailable, available > 0)
                    .Set(b => b.UpdatedAt, now)
                    .Inc(b => b.ReferenceVersion, 1),
                cancellationToken: ct);

            return false;
        }
    }

    private static async Task RecordHistoryAsync(
        IMongoCollection<CirculationHistoryEntry> history,
        IClientSessionHandle tx,
        string entityType,
        string entityId,
        string bookId,
        string userId,
        string? fromStatus,
        string toStatus,
        string? actorId,
        string? actorRole,
        DateTime timestamp,
        string? reason,
        CancellationToken ct)
    {
        var entry = new CirculationHistoryEntry
        {
            EntityType = entityType,
            EntityId = entityId,
            BookId = bookId,
            UserId = userId,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ActorId = actorId,
            ActorRole = actorRole,
            Timestamp = timestamp,
            Reason = reason
        };
        await history.InsertOneAsync(tx, entry, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<CirculationHistoryEntry>> GetHistoryAsync(string entityId, CancellationToken ct)
    {
        return await _history.Find(h => h.EntityId == entityId)
            .SortByDescending(h => h.Timestamp)
            .Limit(100)
            .ToListAsync(ct);
    }

    #endregion
}
