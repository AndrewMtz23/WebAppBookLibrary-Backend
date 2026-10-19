using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Reviews;
namespace WebAppBookLibrary.Tests;
public sealed class ReviewConcurrencyTests
{
 [LocalMongoFact] public Task Concurrent_publications_on_distinct_books_share_one_twenty_per_hour_quota()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var books=Enumerable.Range(0,21).Select(_=>new Book{Id=ObjectId.GenerateNewId().ToString(),IsActive=true}).ToArray();await f.Db.Books.InsertManyAsync(books);
  var store=new MongoReviewStore(f.Db,Options.Create(new ReviewOptions()),new SupportPersistenceTests.Clock { Now = DateTime.UtcNow.AddDays(1) });
  var results=await Task.WhenAll(books.Select(b=>store.SaveAsync(f.User.Id,b.Id,new(4,"Text",0),default)));
  Assert.Equal(20,results.Count(r=>r.Status==200));Assert.Single(results,r=>r.Status==429);
 });
 [LocalMongoFact] public Task Removal_and_restoration_with_same_version_have_one_winner_and_never_resurrect_removed_text()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var o=Options.Create(new ReviewOptions());var store=new MongoReviewStore(f.Db,o,TimeProvider.System);var mod=new ReviewModerationStore(f.Db,o,TimeProvider.System);var admin=await SupportPersistenceTests.UserAsync(f.Db,"admin");
  var review=(await store.SaveAsync(f.User.Id,f.Book.Id,new(5,"Text",0),default)).Value!;await mod.SetVisibilityAsync(admin.Id,review.Id,new("hidden","Reason",1),default);
  var results=await Task.WhenAll(store.RemoveAsync(f.User.Id,f.Book.Id,new(2),default),mod.SetVisibilityAsync(admin.Id,review.Id,new("visible","Reason",2),default));Assert.Equal(new[]{200,409},results.Select(r=>r.Status).Order());
  var own=(await new ReviewQueries(f.Db).MineAsync(f.User.Id,f.Book.Id,default)).Value!;
  if(own.Visibility!="removed")await store.RemoveAsync(f.User.Id,f.Book.Id,new(own.Version),default);
  own=(await new ReviewQueries(f.Db).MineAsync(f.User.Id,f.Book.Id,default)).Value!;
  await mod.SetVisibilityAsync(admin.Id,review.Id,new("visible","Reason",own.Version),default);
  own=(await new ReviewQueries(f.Db).MineAsync(f.User.Id,f.Book.Id,default)).Value!;Assert.Equal("removed",own.Visibility);Assert.Null(own.Rating);Assert.Empty(own.Text);
 });
 [LocalMongoFact] public Task Role_changes_reject_later_publications_and_leave_no_partial_rate_event()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var store=new MongoReviewStore(f.Db,Options.Create(new ReviewOptions()),TimeProvider.System);
  await Task.WhenAll(store.SaveAsync(f.User.Id,f.Book.Id,new(4,"Text",0),default),f.Db.Users.UpdateOneAsync(u=>u.Id==f.User.Id,Builders<User>.Update.Set(u=>u.Role,"librarian")));
  var before=await db.GetCollection<ReviewRateEvent>("ReviewRateEvents").CountDocumentsAsync(FilterDefinition<ReviewRateEvent>.Empty);
  Assert.Equal(403,(await store.SaveAsync(f.User.Id,f.Book.Id,new(4,"Changed",1),default)).Status);
  Assert.Equal(before,await db.GetCollection<ReviewRateEvent>("ReviewRateEvents").CountDocumentsAsync(FilterDefinition<ReviewRateEvent>.Empty));
 });
 [LocalMongoFact] public Task Report_quota_is_shared_across_distinct_reviews_under_concurrency()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var rows=Enumerable.Range(0,11).Select(_=>new Review{UserId=ObjectId.GenerateNewId().ToString(),BookId=f.Book.Id,Rating=4,Visibility="visible"}).ToArray();await db.GetCollection<Review>("Reviews").InsertManyAsync(rows);
  var mod=new ReviewModerationStore(f.Db,Options.Create(new ReviewOptions()),new SupportPersistenceTests.Clock{Now=DateTime.UtcNow.AddDays(1)});
  var results=await Task.WhenAll(rows.Select(r=>mod.ReportAsync(f.User.Id,r.Id,new("Spam"),default)));
  Assert.Equal(10,results.Count(r=>r.Status==200));Assert.Single(results,r=>r.Status==429);
  Assert.Equal(11,await db.GetCollection<Review>("Reviews").CountDocumentsAsync(r=>r.Visibility=="visible"));
 });
}
