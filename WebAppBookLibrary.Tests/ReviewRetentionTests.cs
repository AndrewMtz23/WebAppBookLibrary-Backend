using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services.Reviews;
namespace WebAppBookLibrary.Tests;
public sealed class ReviewRetentionTests
{
 [LocalMongoFact] public Task Anniversary_purge_preserves_current_hold_and_open_reports()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var date=new DateTime(2024,2,29,12,0,0,DateTimeKind.Utc);
  var r=new Review{UserId=f.User.Id,BookId=f.Book.Id,ModerationHold=true,ModerationReason="Current",Visibility="hidden",Rating=4};await db.GetCollection<Review>("Reviews").InsertOneAsync(r);
  await db.GetCollection<ReviewModerationHistory>("ReviewModerationHistory").InsertOneAsync(new(){ReviewId=r.Id,CreatedAt=date,Reason="Old"});
  await db.GetCollection<ReviewReport>("ReviewReports").InsertManyAsync(new[]{new ReviewReport{UserId="one",ReviewId=r.Id,Status="resolved",ResolvedAt=date},new ReviewReport{UserId="two",ReviewId=r.Id,Status="open",CreatedAt=date}});
  var retention=new ReviewRetention(f.Db,Options.Create(new ReviewOptions()));
  Assert.Equal(0,await retention.PurgeExpiredAsync(new(2025,2,28,11,59,59,DateTimeKind.Utc),100,default));
  Assert.Equal(2,await retention.PurgeExpiredAsync(new(2025,2,28,12,0,0,DateTimeKind.Utc),100,default));
  Assert.True((await db.GetCollection<Review>("Reviews").Find(x=>x.Id==r.Id).FirstAsync()).ModerationHold);
  Assert.Equal(1,await db.GetCollection<ReviewReport>("ReviewReports").CountDocumentsAsync(FilterDefinition<ReviewReport>.Empty));
 });
 [LocalMongoFact] public Task Account_cleanup_removes_owned_content_and_anonymizes_other_decisions()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var other=await SupportPersistenceTests.UserAsync(f.Db,"user");
  var owned=new Review{UserId=f.User.Id,BookId=f.Book.Id};var foreign=new Review{UserId=other.Id,BookId=f.Book.Id,ModeratorId=f.User.Id,ModerationHold=true,ModerationReason="Personal data"};await db.GetCollection<Review>("Reviews").InsertManyAsync(new[]{owned,foreign});
  await db.GetCollection<ReviewReport>("ReviewReports").InsertManyAsync(new[]{new ReviewReport{UserId=other.Id,ReviewId=owned.Id},new ReviewReport{UserId=f.User.Id,ReviewId=foreign.Id}});
  using var session=await db.Client.StartSessionAsync();await session.WithTransactionAsync(async(tx,ct)=>{await ReviewRetention.DeleteOwnedAsync(db,tx,f.User.Id,ct);await ReviewRetention.AnonymizeActorAsync(db,tx,f.User.Id,ct);return true;});
  Assert.False(await db.GetCollection<Review>("Reviews").Find(r=>r.Id==owned.Id).AnyAsync());Assert.Equal(0,await db.GetCollection<ReviewReport>("ReviewReports").CountDocumentsAsync(FilterDefinition<ReviewReport>.Empty));
  var r=await db.GetCollection<Review>("Reviews").Find(r=>r.Id==foreign.Id).FirstAsync();Assert.True(r.ModerationHold);Assert.Null(r.ModeratorId);Assert.Equal("Decisión administrativa",r.ModerationReason);
 });
}
