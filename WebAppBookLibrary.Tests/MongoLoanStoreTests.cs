using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public class MongoLoanStoreTests
{
    [Fact]
    public async Task ReserveAvailableBook_uses_identifier_and_availability_in_one_atomic_update()
    {
        const string bookId = "507f1f77bcf86cd799439011";
        var books = new Mock<IMongoCollection<Book>>();
        var loans = new Mock<IMongoCollection<Loan>>();
        var users = new Mock<IMongoCollection<User>>();
        FilterDefinition<Book>? filter = null;
        UpdateDefinition<Book>? update = null;
        FindOneAndUpdateOptions<Book, Book>? options = null;
        books.Setup(x => x.FindOneAndUpdateAsync(
                It.IsAny<FilterDefinition<Book>>(),
                It.IsAny<UpdateDefinition<Book>>(),
                It.IsAny<FindOneAndUpdateOptions<Book, Book>>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<Book>, UpdateDefinition<Book>, FindOneAndUpdateOptions<Book, Book>, CancellationToken>(
                (capturedFilter, capturedUpdate, capturedOptions, _) =>
                {
                    filter = capturedFilter;
                    update = capturedUpdate;
                    options = capturedOptions;
                })
            .ReturnsAsync(new Book { Id = bookId, IsAvailable = true });
        var store = new MongoLoanStore(books.Object, loans.Object, users.Object);

        var reserved = await store.ReserveAvailableBookAsync(bookId);

        Assert.NotNull(reserved);
        Assert.NotNull(filter);
        Assert.NotNull(update);
        var renderedFilter = Render(filter);
        var renderedUpdate = Render(update);
        Assert.Equal(ObjectId.Parse(bookId), renderedFilter["_id"].AsObjectId);
        Assert.True(renderedFilter["IsAvailable"].AsBoolean);
        Assert.False(renderedUpdate["$set"]["IsAvailable"].AsBoolean);
        Assert.Equal(ReturnDocument.Before, options!.ReturnDocument);
    }

    [Fact]
    public async Task MarkReturned_uses_identifier_and_active_state_in_one_conditional_update()
    {
        const string loanId = "507f1f77bcf86cd799439012";
        var books = new Mock<IMongoCollection<Book>>();
        var loans = new Mock<IMongoCollection<Loan>>();
        var users = new Mock<IMongoCollection<User>>();
        FilterDefinition<Loan>? filter = null;
        loans.Setup(x => x.UpdateOneAsync(
                It.IsAny<FilterDefinition<Loan>>(),
                It.IsAny<UpdateDefinition<Loan>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<Loan>, UpdateDefinition<Loan>, UpdateOptions, CancellationToken>(
                (capturedFilter, _, _, _) => filter = capturedFilter)
            .ReturnsAsync(Mock.Of<UpdateResult>(result => result.ModifiedCount == 1));
        var store = new MongoLoanStore(books.Object, loans.Object, users.Object);

        var marked = await store.MarkReturnedAsync(loanId, new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(marked);
        Assert.NotNull(filter);
        var renderedFilter = Render(filter);
        Assert.Equal(ObjectId.Parse(loanId), renderedFilter["_id"].AsObjectId);
        Assert.False(renderedFilter["IsReturned"].AsBoolean);
    }

    [Fact]
    public async Task RestoreBookAvailability_returns_false_when_book_does_not_exist()
    {
        const string bookId = "507f1f77bcf86cd799439011";
        var books = new Mock<IMongoCollection<Book>>();
        var loans = new Mock<IMongoCollection<Loan>>();
        var users = new Mock<IMongoCollection<User>>();
        books.Setup(x => x.UpdateOneAsync(
                It.IsAny<FilterDefinition<Book>>(),
                It.IsAny<UpdateDefinition<Book>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<UpdateResult>(result => result.MatchedCount == 0));
        var store = new MongoLoanStore(books.Object, loans.Object, users.Object);

        var restored = await store.RestoreBookAvailabilityAsync(bookId);

        Assert.False(restored);
    }

    [Fact]
    public async Task RestoreBookAvailability_returns_true_when_book_was_already_available()
    {
        const string bookId = "507f1f77bcf86cd799439011";
        var books = new Mock<IMongoCollection<Book>>();
        var loans = new Mock<IMongoCollection<Loan>>();
        var users = new Mock<IMongoCollection<User>>();
        FilterDefinition<Book>? filter = null;
        books.Setup(x => x.UpdateOneAsync(
                It.IsAny<FilterDefinition<Book>>(),
                It.IsAny<UpdateDefinition<Book>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<Book>, UpdateDefinition<Book>, UpdateOptions, CancellationToken>(
                (capturedFilter, _, _, _) => filter = capturedFilter)
            .ReturnsAsync(Mock.Of<UpdateResult>(result =>
                result.MatchedCount == 1 && result.ModifiedCount == 0));
        var store = new MongoLoanStore(books.Object, loans.Object, users.Object);

        var restored = await store.RestoreBookAvailabilityAsync(bookId);

        Assert.True(restored);
        Assert.NotNull(filter);
        var renderedFilter = Render(filter);
        Assert.Equal(1, renderedFilter.ElementCount);
        Assert.Equal(ObjectId.Parse(bookId), renderedFilter["_id"].AsObjectId);
    }

    [Fact]
    public async Task Invalid_identifiers_do_not_reach_mongo()
    {
        var books = new Mock<IMongoCollection<Book>>();
        var loans = new Mock<IMongoCollection<Loan>>();
        var users = new Mock<IMongoCollection<User>>();
        var store = new MongoLoanStore(books.Object, loans.Object, users.Object);

        Assert.Null(await store.ReserveAvailableBookAsync("not-an-object-id"));
        Assert.False(await store.RestoreBookAvailabilityAsync("not-an-object-id"));
        Assert.Null(await store.FindActiveLoanAsync("not-an-object-id"));
        Assert.Null(await store.FindLoanAsync("not-an-object-id"));
        Assert.False(await store.MarkReturnedAsync("not-an-object-id", DateTime.UtcNow));

        books.VerifyNoOtherCalls();
        loans.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InsertLoan_rejects_invalid_foreign_identifiers_before_mongo()
    {
        var books = new Mock<IMongoCollection<Book>>();
        var loans = new Mock<IMongoCollection<Loan>>();
        var users = new Mock<IMongoCollection<User>>();
        var store = new MongoLoanStore(books.Object, loans.Object, users.Object);

        await Assert.ThrowsAsync<ArgumentException>(() => store.InsertLoanAsync(new Loan
        {
            BookId = "not-an-object-id",
            UserId = "also-invalid"
        }));

        loans.VerifyNoOtherCalls();
    }

    private static BsonDocument Render<T>(FilterDefinition<T> definition)
    {
        var registry = BsonSerializer.SerializerRegistry;
        return definition.Render(new RenderArgs<T>(registry.GetSerializer<T>(), registry)).AsBsonDocument;
    }

    private static BsonDocument Render<T>(UpdateDefinition<T> definition)
    {
        var registry = BsonSerializer.SerializerRegistry;
        return definition.Render(new RenderArgs<T>(registry.GetSerializer<T>(), registry)).AsBsonDocument;
    }
}
