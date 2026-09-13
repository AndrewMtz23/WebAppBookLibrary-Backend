using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Dashboard;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class MongoDashboardStore(MongoDBService database) : IDashboardStore
{
    private readonly IMongoCollection<User> _users = database.Users;
    private readonly IMongoCollection<Book> _books = database.Books;
    private readonly IMongoCollection<Loan> _loans = database.Loans;
    private readonly IMongoCollection<Favorite> _favorites = database.Favorites;
    private readonly IMongoCollection<LogEntry> _logs = database.LogEntries;

    public async Task<User?> FindActiveUserAsync(string username, CancellationToken token) =>
        await _users.Find(user => user.Username == username && user.IsActive).FirstOrDefaultAsync(token);

    public async Task<ReaderDashboardResponse> ReaderAsync(string userId, DashboardPeriod period, CancellationToken token)
    {
        var filter = Builders<Loan>.Filter.Eq(loan => loan.UserId, userId) &
                     Builders<Loan>.Filter.Gte(loan => loan.ReservedAt, period.FromUtc) &
                     Builders<Loan>.Filter.Lt(loan => loan.ReservedAt, period.ToUtc);
        var total = await _loans.CountDocumentsAsync(filter, cancellationToken: token);
        var favorites = await _favorites.CountDocumentsAsync(item => item.UserId == userId, cancellationToken: token);
        var media = await _loans.Aggregate().Match(filter)
            .Group(loan => loan.MediaType, group => new MetricCount(group.Key, group.LongCount()))
            .SortBy(item => item.Key).ToListAsync(token);
        return new(period.GeneratedAt, period.FromUtc, period.ToUtc, total, favorites, media);
    }

    public async Task<LibrarianDashboardResponse> LibrarianAsync(DashboardPeriod period, CancellationToken token)
    {
        var periodCounts = await ReservationPeriodCounts(period, token);
        var previousCounts = await ReservationPeriodCounts(period with { FromUtc = period.PreviousFromUtc, ToUtc = period.PreviousToUtc }, token);
        var currentPhysical = await CountLoans(new BsonDocument
        {
            { "_mediaType", "physical" },
            { "_status", new BsonDocument("$in", new BsonArray { "active", "overdue" }) }
        }, period, token);
        var overduePhysical = await CountLoans(new BsonDocument { { "_mediaType", "physical" }, { "_status", "overdue" } }, period, token);
        var returnedCurrent = await CountLoans(And(new BsonDocument("_mediaType", "physical"), new BsonDocument("_status", "returned"), Range("_returnedAt", period.FromUtc, period.ToUtc)), period, token);
        var returnedPrevious = await CountLoans(And(new BsonDocument("_mediaType", "physical"), new BsonDocument("_status", "returned"), Range("_returnedAt", period.PreviousFromUtc, period.PreviousToUtc)), period, token);
        var inventory = await InventorySummary(token);
        return new(
            period.GeneratedAt, period.FromUtc, period.ToUtc, periodCounts.Total, currentPhysical, overduePhysical,
            await _books.CountDocumentsAsync(ActiveBookFilter(), cancellationToken: token), inventory.Available,
            periodCounts.ByMedia, period.Timezone, period.PreviousFromUtc, period.PreviousToUtc,
            DashboardComparison.From(returnedCurrent, returnedPrevious), DashboardComparison.From(periodCounts.Digital, previousCounts.Digital),
            inventory.Low, inventory.Out, await TopReserved(period, token), await WithoutReservations(period, token));
    }

    public async Task<AdminDashboardResponse> AdminAsync(DashboardPeriod period, CancellationToken token)
    {
        var current = await ReservationPeriodCounts(period, token);
        var previous = await ReservationPeriodCounts(period with { FromUtc = period.PreviousFromUtc, ToUtc = period.PreviousToUtc }, token);
        var outstanding = await CountLoans(new BsonDocument("_status", new BsonDocument("$in", new BsonArray { "active", "overdue" })), period, token);
        var overdue = await CountLoans(new BsonDocument("_status", "overdue"), period, token);
        var inventory = await InventorySummary(token);
        var usersByRole = await _users.Aggregate().Match(user => user.IsActive)
            .Group(user => user.Role, group => new MetricCount(group.Key, group.LongCount())).SortBy(item => item.Key).ToListAsync(token);
        return new(
            period.GeneratedAt, period.FromUtc, period.ToUtc,
            await _users.CountDocumentsAsync(user => user.IsActive, cancellationToken: token),
            await _books.CountDocumentsAsync(_ => true, cancellationToken: token), current.Total, usersByRole, current.ByMedia,
            period.Timezone, period.PreviousFromUtc, period.PreviousToUtc,
            await _books.CountDocumentsAsync(ActiveBookFilter(), cancellationToken: token), outstanding, overdue,
            DashboardComparison.From(current.Total, previous.Total), await DailyReservations(period, token),
            await ActiveBookGenres(token), await ActiveBookMedia(token), await TopReserved(period, token),
            await _users.CountDocumentsAsync(user => !user.IsActive, cancellationToken: token), inventory.Low + inventory.Out,
            await InventoryAttention(token), inventory.Low, inventory.Out);
    }

    public async Task<IReadOnlyList<DashboardActivityItem>> AdminActivityAsync(DashboardPeriod period, CancellationToken token) =>
        await _logs.Find(Builders<LogEntry>.Filter.Gte(item => item.Timestamp, period.FromUtc) &
                         Builders<LogEntry>.Filter.Lt(item => item.Timestamp, period.ToUtc) &
                         Builders<LogEntry>.Filter.Regex(item => item.EventType, new BsonRegularExpression("^(book\\.|user\\.(role_change_attempt|status_change_attempt)$)")))
            .SortByDescending(item => item.Timestamp).ThenByDescending(item => item.Id).Limit(10)
            .Project(item => new DashboardActivityItem(item.Id, item.EventType!, item.Timestamp, item.ActorUsername, item.TargetType, item.TargetId))
            .ToListAsync(token);

    private async Task<(long Total, long Digital, IReadOnlyList<MetricCount> ByMedia)> ReservationPeriodCounts(DashboardPeriod period, CancellationToken token)
    {
        var rows = await LoanAggregate([
            new("$match", Range("_reservedAt", period.FromUtc, period.ToUtc)),
            new("$group", new BsonDocument { { "_id", "$_mediaType" }, { "count", new BsonDocument("$sum", 1) } }),
            new("$sort", new BsonDocument("_id", 1))
        ], token, period.GeneratedAt);
        var counts = rows.Select(row => new MetricCount(row["_id"].AsString, row["count"].ToInt64())).ToArray();
        return (counts.Sum(item => item.Count), counts.FirstOrDefault(item => item.Key == "digital")?.Count ?? 0, counts);
    }

    private async Task<long> CountLoans(BsonDocument match, DashboardPeriod period, CancellationToken token)
    {
        var rows = await LoanAggregate([new("$match", match), new("$count", "value")], token, period.GeneratedAt);
        return rows.Count == 0 ? 0 : rows[0]["value"].ToInt64();
    }

    private async Task<IReadOnlyList<DashboardSeriesPoint>> DailyReservations(DashboardPeriod period, CancellationToken token)
    {
        var rows = await LoanAggregate([
            new("$match", Range("_reservedAt", period.FromUtc, period.ToUtc)),
            new("$group", new BsonDocument
            {
                { "_id", new BsonDocument { { "date", new BsonDocument("$dateToString", new BsonDocument { { "format", "%Y-%m-%d" }, { "date", "$_reservedAt" }, { "timezone", period.Timezone } }) }, { "media", "$_mediaType" } } },
                { "count", new BsonDocument("$sum", 1) }
            })
        ], token, period.GeneratedAt);
        var counts = rows.ToDictionary(row => (row["_id"]["date"].AsString, row["_id"]["media"].AsString), row => row["count"].ToInt64());
        var zone = TimeZoneInfo.FindSystemTimeZoneById(period.Timezone);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(period.FromUtc, zone));
        return Enumerable.Range(0, period.LocalCalendarDays).Select(offset =>
        {
            var date = first.AddDays(offset);
            var key = date.ToString("yyyy-MM-dd");
            return new DashboardSeriesPoint(date, counts.GetValueOrDefault((key, "physical")), counts.GetValueOrDefault((key, "digital")));
        }).ToArray();
    }

    private async Task<IReadOnlyList<DashboardRankItem>> TopReserved(DashboardPeriod period, CancellationToken token)
    {
        var stages = EffectiveLoanStages(period.GeneratedAt).Concat([
            new BsonDocument("$match", Range("_reservedAt", period.FromUtc, period.ToUtc)),
            new BsonDocument("$group", new BsonDocument { { "_id", "$BookId" }, { "count", new BsonDocument("$sum", 1) } }),
            BookLookup(), new BsonDocument("$unwind", "$_book"),
            new BsonDocument("$match", new BsonDocument("_book.IsActive", new BsonDocument("$ne", false))),
            new BsonDocument("$sort", new BsonDocument { { "count", -1 }, { "_id", 1 } }),
            new BsonDocument("$limit", 5),
            new BsonDocument("$project", new BsonDocument { { "id", "$_id" }, { "label", "$_book.Title" }, { "count", 1 }, { "_id", 0 } })
        ]);
        var rows = await _loans.Aggregate<BsonDocument>(stages.ToArray()).ToListAsync(token);
        return rows.Select(row => new DashboardRankItem(row["id"].AsString, row["label"].AsString, row["count"].ToInt64())).ToArray();
    }

    private async Task<IReadOnlyList<DashboardRankItem>> WithoutReservations(DashboardPeriod period, CancellationToken token)
    {
        var lookupPipeline = new BsonArray(EffectiveLoanStages(period.GeneratedAt).Concat([
            new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { "$BookId", "$$bookId" }),
                new BsonDocument("$gte", new BsonArray { "$_reservedAt", period.FromUtc }),
                new BsonDocument("$lt", new BsonArray { "$_reservedAt", period.ToUtc })
            }))),
            new BsonDocument("$limit", 1)
        ]));
        var rows = await _books.Aggregate<BsonDocument>(new BsonDocument[]
        {
            new("$match", ActiveBookDocument()),
            new("$lookup", new BsonDocument { { "from", _loans.CollectionNamespace.CollectionName }, { "let", new BsonDocument("bookId", new BsonDocument("$toString", "$_id")) }, { "pipeline", lookupPipeline }, { "as", "_reservations" } }),
            new("$match", new BsonDocument("_reservations", new BsonDocument("$size", 0))),
            new("$sort", new BsonDocument { { "Title", 1 }, { "_id", 1 } }), new("$limit", 5),
            new("$project", new BsonDocument { { "id", new BsonDocument("$toString", "$_id") }, { "label", "$Title" }, { "count", new BsonDocument("$literal", new BsonInt64(0)) }, { "_id", 0 } })
        }).ToListAsync(token);
        return rows.Select(row => new DashboardRankItem(row["id"].AsString, row["label"].AsString, row["count"].ToInt64())).ToArray();
    }

    private async Task<(long Available, long Low, long Out)> InventorySummary(CancellationToken token)
    {
        var rows = await _books.Aggregate<BsonDocument>(InventoryStages().Concat([
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", BsonNull.Value }, { "available", new BsonDocument("$sum", "$_available") },
                { "low", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$_available", 1 }), 1, 0 })) },
                { "out", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$_available", 0 }), 1, 0 })) }
            })
        ]).ToArray()).ToListAsync(token);
        return rows.Count == 0 ? (0, 0, 0) : (rows[0]["available"].ToInt64(), rows[0]["low"].ToInt64(), rows[0]["out"].ToInt64());
    }

    private async Task<IReadOnlyList<DashboardBookAttention>> InventoryAttention(CancellationToken token)
    {
        var rows = await _books.Aggregate<BsonDocument>(InventoryStages().Concat([
            new BsonDocument("$match", new BsonDocument("_available", new BsonDocument("$lte", 1))),
            new BsonDocument("$sort", new BsonDocument { { "_available", 1 }, { "Title", 1 }, { "_id", 1 } }),
            new BsonDocument("$limit", 5),
            new BsonDocument("$project", new BsonDocument { { "id", new BsonDocument("$toString", "$_id") }, { "title", "$Title" }, { "available", "$_available" }, { "_id", 0 } })
        ]).ToArray()).ToListAsync(token);
        return rows.Select(row => new DashboardBookAttention(row["id"].AsString, row["title"].AsString, row["available"].ToInt64())).ToArray();
    }

    private async Task<IReadOnlyList<MetricCount>> ActiveBookMedia(CancellationToken token)
    {
        var rows = await _books.Aggregate<BsonDocument>(new BsonDocument[]
        {
            new("$match", ActiveBookDocument()), new("$set", new BsonDocument("_media", EffectiveMediaExpression())),
            new("$group", new BsonDocument { { "_id", "$_media" }, { "count", new BsonDocument("$sum", 1) } }), new("$sort", new BsonDocument("_id", 1))
        }).ToListAsync(token);
        return ToMetricCounts(rows);
    }

    private async Task<IReadOnlyList<MetricCount>> ActiveBookGenres(CancellationToken token)
    {
        var rows = await _books.Aggregate<BsonDocument>(new BsonDocument[]
        {
            new("$match", ActiveBookDocument()),
            new("$set", new BsonDocument("_genres", new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", new BsonDocument("$ifNull", new BsonArray { "$Genres", new BsonArray() })), 0 }),
                "$Genres", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$Genre", "Sin género" }) }
            }))),
            new("$unwind", "$_genres"), new("$group", new BsonDocument { { "_id", "$_genres" }, { "count", new BsonDocument("$sum", 1) } }),
            new("$sort", new BsonDocument { { "count", -1 }, { "_id", 1 } }), new("$limit", 10)
        }).ToListAsync(token);
        return ToMetricCounts(rows);
    }

    private async Task<List<BsonDocument>> LoanAggregate(IEnumerable<BsonDocument> stages, CancellationToken token, DateTime now) =>
        await _loans.Aggregate<BsonDocument>(EffectiveLoanStages(now).Concat(stages).ToArray()).ToListAsync(token);

    private static IEnumerable<BsonDocument> EffectiveLoanStages(DateTime now) =>
    [
        new("$set", new BsonDocument
        {
            { "_legacyMedia", BlankStringExpression("$MediaType") },
            { "_legacyStatus", BlankStringExpression("$Status") },
            { "_reservedAt", new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$or", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$ReservedAt", BsonNull.Value }), BsonNull.Value }),
                        new BsonDocument("$eq", new BsonArray { "$ReservedAt", DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc) })
                    }),
                    new BsonDocument("$ifNull", new BsonArray { "$LoanDate", now }), "$ReservedAt"
                }) },
            { "_returnedAt", new BsonDocument("$ifNull", new BsonArray { "$ReturnedAt", "$ReturnDate" }) }
        }),
        new("$set", new BsonDocument
        {
            { "_mediaType", new BsonDocument("$cond", new BsonArray { "$_legacyMedia", "physical", "$MediaType" }) },
            { "_dueAt", new BsonDocument("$ifNull", new BsonArray { "$DueAt", new BsonDocument("$cond", new BsonArray { "$_legacyMedia", new BsonDocument("$dateAdd", new BsonDocument { { "startDate", new BsonDocument("$ifNull", new BsonArray { "$LoanDate", now }) }, { "unit", "day" }, { "amount", 14 } }), BsonNull.Value }) }) }
        }),
        new("$set", new BsonDocument("_status", new BsonDocument("$cond", new BsonArray
        {
            "$_legacyStatus",
            new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { "$IsReturned", true }), "returned",
                new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$and", new BsonArray { new BsonDocument("$ne", new BsonArray { "$_dueAt", BsonNull.Value }), new BsonDocument("$lt", new BsonArray { "$_dueAt", now }) }),
                    "overdue", "active"
                })
            }),
            new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { "$Status", "active" }),
                    new BsonDocument("$ne", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$DueAt", BsonNull.Value }), BsonNull.Value }),
                    new BsonDocument("$lt", new BsonArray { "$DueAt", now })
                }),
                "overdue", "$Status"
            })
        })))
    ];

    private static BsonDocument BlankStringExpression(string field) => new("$regexMatch", new BsonDocument
    {
        { "input", new BsonDocument("$ifNull", new BsonArray { field, string.Empty }) },
        { "regex", "^\\s*$" }
    });

    private static IEnumerable<BsonDocument> InventoryStages() =>
    [
        new("$match", ActiveBookDocument()),
        new("$set", new BsonDocument
        {
            { "_media", EffectiveMediaExpression() },
            { "_available", EffectiveAvailableExpression() }
        }),
        new("$match", new BsonDocument("_media", "physical"))
    ];

    private BsonDocument BookLookup() => new("$lookup", new BsonDocument
    {
        { "from", _books.CollectionNamespace.CollectionName }, { "let", new BsonDocument("bookId", "$_id") },
        { "pipeline", new BsonArray { new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { new BsonDocument("$toString", "$_id"), "$$bookId" }))) } }, { "as", "_book" }
    });

    private static FilterDefinition<Book> ActiveBookFilter() => Builders<Book>.Filter.Or(Builders<Book>.Filter.Eq(book => book.IsActive, true), Builders<Book>.Filter.Exists(book => book.IsActive, false));
    private static BsonDocument ActiveBookDocument() => new("$or", new BsonArray { new BsonDocument("IsActive", true), new BsonDocument("IsActive", new BsonDocument("$exists", false)) });
    private static BsonValue EffectiveMediaExpression() => new BsonDocument("$cond", new BsonArray { BlankStringExpression("$MediaType"), "physical", "$MediaType" });
    private static BsonValue EffectiveAvailableExpression() => new BsonDocument("$ifNull", new BsonArray
    {
        "$AvailableCopies",
        new BsonDocument("$cond", new BsonArray { "$IsAvailable", 1, 0 })
    });
    private static BsonDocument Range(string field, DateTime from, DateTime to) => new(field, new BsonDocument { { "$gte", from }, { "$lt", to } });
    private static BsonDocument And(params BsonDocument[] filters) => new("$and", new BsonArray(filters));
    private static IReadOnlyList<MetricCount> ToMetricCounts(IEnumerable<BsonDocument> rows) => rows.Select(row => new MetricCount(row["_id"].AsString, row["count"].ToInt64())).ToArray();
}
