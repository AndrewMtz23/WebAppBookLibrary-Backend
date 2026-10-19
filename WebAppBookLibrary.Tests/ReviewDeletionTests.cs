using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Reviews;
namespace WebAppBookLibrary.Tests;
public sealed class ReviewDeletionTests
{
 [LocalMongoFact] public Task Actual_account_deletion_racing_publication_and_reporting_leaves_no_orphans()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var o=Options.Create(new ReviewOptions());var store=new MongoReviewStore(f.Db,o,TimeProvider.System);var mod=new ReviewModerationStore(f.Db,o,TimeProvider.System);var admin=await SupportPersistenceTests.UserAsync(f.Db,"admin");var accounts=new MongoAdminUserStore(f.Db);
  var reporter=await SupportPersistenceTests.UserAsync(f.Db,"user");await f.Db.Users.UpdateOneAsync(u=>u.Id==reporter.Id,Builders<User>.Update.Set(u=>u.EmailVerifiedAt,DateTime.UtcNow));
  var created=(await store.SaveAsync(f.User.Id,f.Book.Id,new(4,"Text",0),default)).Value!;
  async Task Delete(){await accounts.SetStatusSafelyAsync(admin.Id,f.User.Id,false,DateTime.UtcNow,default);Assert.Equal(AdminStoreMutationOutcome.Success,(await accounts.DeletePermanentlyAsync(admin.Id,f.User.Id,default)).Outcome);}
  await Task.WhenAll(Delete(),store.SaveAsync(f.User.Id,f.Book.Id,new(5,"Changed",1),default),mod.ReportAsync(reporter.Id,created.Id,new("Report"),default));
  Assert.False(await db.GetCollection<Review>("Reviews").Find(r=>r.UserId==f.User.Id).AnyAsync());Assert.False(await db.GetCollection<ReviewReport>("ReviewReports").Find(r=>r.ReviewId==created.Id).AnyAsync());
  Assert.Equal(401,(await store.SaveAsync(f.User.Id,f.Book.Id,new(3,"Again",0),default)).Status);
 });
 [LocalMongoFact] public Task Actual_book_delete_serializes_with_creation_and_cleans_notification_outbox()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var o=Options.Create(new ReviewOptions());var store=new MongoReviewStore(f.Db,o,TimeProvider.System);var mod=new ReviewModerationStore(f.Db,o,TimeProvider.System);var admin=await SupportPersistenceTests.UserAsync(f.Db,"admin");
  var review=(await store.SaveAsync(f.User.Id,f.Book.Id,new(4,"Text",0),default)).Value!;await mod.SetVisibilityAsync(admin.Id,review.Id,new("hidden","Reason",1),default);
  var notice=await db.GetCollection<Notification>("Notifications").Find(n=>n.TargetId==review.Id).FirstAsync();await db.GetCollection<AccountMailJob>("AccountMailOutbox").InsertOneAsync(new(){NotificationId=notice.Id});
  var books=new MongoBookStore(f.Db);
  async Task Delete(){await books.SetActiveAsync(f.Book.Id,false,DateTime.UtcNow,default);Assert.True((await books.DeletePermanentlyAsync(f.Book.Id,default)).Success);}
  await Task.WhenAll(Delete(),store.SaveAsync(f.User.Id,f.Book.Id,new(5,"Changed",2),default));
  Assert.False(await db.GetCollection<Review>("Reviews").Find(r=>r.BookId==f.Book.Id).AnyAsync());Assert.False(await db.GetCollection<Notification>("Notifications").Find(n=>n.TargetId==review.Id).AnyAsync());Assert.False(await db.GetCollection<AccountMailJob>("AccountMailOutbox").Find(j=>j.NotificationId==notice.Id).AnyAsync());
 });
 [LocalMongoFact] public Task Aborted_cleanup_transaction_preserves_content_and_outbox()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var r=new Review{UserId=f.User.Id,BookId=f.Book.Id};await db.GetCollection<Review>("Reviews").InsertOneAsync(r);
  var n=new Notification{UserId=f.User.Id,TargetType="review",TargetId=r.Id};await db.GetCollection<Notification>("Notifications").InsertOneAsync(n);await db.GetCollection<AccountMailJob>("AccountMailOutbox").InsertOneAsync(new(){NotificationId=n.Id});
  using var tx=await db.Client.StartSessionAsync();tx.StartTransaction();await ReviewRetention.DeleteOwnedAsync(db,tx,f.User.Id,default);await tx.AbortTransactionAsync();
  Assert.True(await db.GetCollection<Review>("Reviews").Find(x=>x.Id==r.Id).AnyAsync());Assert.True(await db.GetCollection<AccountMailJob>("AccountMailOutbox").Find(j=>j.NotificationId==n.Id).AnyAsync());
 });
}
