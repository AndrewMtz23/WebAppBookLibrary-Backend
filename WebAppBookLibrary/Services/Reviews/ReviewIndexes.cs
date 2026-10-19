using MongoDB.Driver;
using WebAppBookLibrary.Models;
namespace WebAppBookLibrary.Services.Reviews;
public static class ReviewIndexes
{
 public static async Task CreateAsync(IMongoDatabase db)
 {
  await db.GetCollection<Review>("Reviews").Indexes.CreateManyAsync(new[]{
   new CreateIndexModel<Review>(Builders<Review>.IndexKeys.Ascending(r=>r.UserId).Ascending(r=>r.BookId),new(){Unique=true}),
   new CreateIndexModel<Review>(Builders<Review>.IndexKeys.Ascending(r=>r.BookId).Ascending(r=>r.Visibility).Descending(r=>r.CreatedAt).Descending(r=>r.Id))});
  await db.GetCollection<ReviewReport>("ReviewReports").Indexes.CreateManyAsync(new[]{
   new CreateIndexModel<ReviewReport>(Builders<ReviewReport>.IndexKeys.Ascending(r=>r.UserId).Ascending(r=>r.ReviewId),new(){Unique=true}),
   new CreateIndexModel<ReviewReport>(Builders<ReviewReport>.IndexKeys.Ascending(r=>r.Status).Ascending(r=>r.ResolvedAt).Descending(r=>r.CreatedAt))});
  await db.GetCollection<ReviewModerationHistory>("ReviewModerationHistory").Indexes.CreateOneAsync(new CreateIndexModel<ReviewModerationHistory>(Builders<ReviewModerationHistory>.IndexKeys.Ascending(r=>r.ReviewId).Descending(r=>r.CreatedAt)));
  await db.GetCollection<ReviewRateEvent>("ReviewRateEvents").Indexes.CreateManyAsync(new[]{new CreateIndexModel<ReviewRateEvent>(Builders<ReviewRateEvent>.IndexKeys.Ascending(r=>r.UserId).Ascending(r=>r.Operation).Ascending(r=>r.CreatedAt)),new CreateIndexModel<ReviewRateEvent>(Builders<ReviewRateEvent>.IndexKeys.Ascending(r=>r.CreatedAt),new(){ExpireAfter=TimeSpan.FromHours(1)})});
 }
}
