using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Reviews;
namespace WebAppBookLibrary.Tests;
public sealed class ReviewPersistenceTests
{
 internal static async Task<(MongoDBService Db,User User,Book Book)> Seed(IMongoDatabase db)
 {
  var f=await ReadingPersistenceTests.Seed(db);
  await f.Db.Users.UpdateOneAsync(u=>u.Id==f.User.Id,Builders<User>.Update.Set(u=>u.DisplayName,"Reader name").Set(u=>u.EmailVerifiedAt,DateTime.UtcNow));
  return(f.Db,f.User,f.Book);
 }
 [LocalMongoFact] public Task Summary_uses_all_visible_reviews_and_current_names_without_private_data()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await Seed(db);var store=new MongoReviewStore(f.Db,Options.Create(new ReviewOptions()),TimeProvider.System);var query=new ReviewQueries(f.Db);
  Assert.Null((await query.PublicAsync(f.Book.Id,new(),default)).Value!.AverageRating);
  Assert.Equal(200,(await store.SaveAsync(f.User.Id,f.Book.Id,new(5,"<script>alert(1)</script>",0),default)).Status);
  var other=await SupportPersistenceTests.UserAsync(f.Db,"user");
  await f.Db.Users.UpdateOneAsync(u=>u.Id==other.Id,Builders<User>.Update.Set(u=>u.DisplayName,"Other").Set(u=>u.EmailVerifiedAt,DateTime.UtcNow));
  Assert.Equal(200,(await store.SaveAsync(other.Id,f.Book.Id,new(3,"",0),default)).Status);
  await f.Db.Users.UpdateOneAsync(u=>u.Id==f.User.Id,Builders<User>.Update.Set(u=>u.DisplayName,"Changed"));
  var page=(await query.PublicAsync(f.Book.Id,new(PageSize:1),default)).Value!;
  Assert.Equal(4d,page.AverageRating);Assert.Equal(2,page.Total);Assert.Single(page.Items);
  var all=(await query.PublicAsync(f.Book.Id,new(),default)).Value!;Assert.Contains(all.Items,x=>x.DisplayName=="Changed");
  var json=System.Text.Json.JsonSerializer.Serialize(all);Assert.DoesNotContain(f.User.Email,json);Assert.DoesNotContain(f.User.Id,json);Assert.DoesNotContain("UserId",json);
 });
 [LocalMongoFact] public Task Concurrent_versions_quotas_and_retirement_are_enforced()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await Seed(db);var clock=new SupportPersistenceTests.Clock { Now = DateTime.UtcNow.AddDays(1) };var store=new MongoReviewStore(f.Db,Options.Create(new ReviewOptions{PublicationsPerHour=2}),clock);
  var creates=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>store.SaveAsync(f.User.Id,f.Book.Id,new(4,"First",0),default)));
  Assert.Equal(new[]{200,409},creates.Select(x=>x.Status).Order());
  var updates=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>store.SaveAsync(f.User.Id,f.Book.Id,new(5,"Second",1),default)));
  Assert.Equal(new[]{200,409},updates.Select(x=>x.Status).Order());
  Assert.Equal(429,(await store.SaveAsync(f.User.Id,f.Book.Id,new(1,"Third",2),default)).Status);
  Assert.Equal(200,(await store.RemoveAsync(f.User.Id,f.Book.Id,new(2),default)).Status);
  Assert.Equal(429,(await store.SaveAsync(f.User.Id,f.Book.Id,new(1,"Again",3),default)).Status);
  var own=(await new ReviewQueries(f.Db).MineAsync(f.User.Id,f.Book.Id,default)).Value!;Assert.Null(own.Rating);Assert.Equal("",own.Text);
  clock.Now=clock.Now.AddHours(1);Assert.Equal(200,(await store.SaveAsync(f.User.Id,f.Book.Id,new(1,"Again",3),default)).Status);
 });
 [LocalMongoFact] public Task Eligibility_and_rollback_flag_do_not_prevent_own_removal()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await Seed(db);var options=new ReviewOptions();var store=new MongoReviewStore(f.Db,Options.Create(options),TimeProvider.System);
  await f.Db.Users.UpdateOneAsync(u=>u.Id==f.User.Id,Builders<User>.Update.Set(u=>u.EmailVerifiedAt,null));
  Assert.Equal(403,(await store.SaveAsync(f.User.Id,f.Book.Id,new(4,"",0),default)).Status);
  await f.Db.Users.UpdateOneAsync(u=>u.Id==f.User.Id,Builders<User>.Update.Set(u=>u.EmailVerifiedAt,DateTime.UtcNow).Set(u=>u.DisplayName,""));
  Assert.Equal(400,(await store.SaveAsync(f.User.Id,f.Book.Id,new(4,"",0),default)).Status);
  await f.Db.Users.UpdateOneAsync(u=>u.Id==f.User.Id,Builders<User>.Update.Set(u=>u.DisplayName,"Name"));
  Assert.Equal(200,(await store.SaveAsync(f.User.Id,f.Book.Id,new(4,"",0),default)).Status);
  options.AcceptContributions=false;Assert.Equal(409,(await store.SaveAsync(f.User.Id,f.Book.Id,new(4,"",1),default)).Status);
  await f.Db.Users.UpdateOneAsync(u=>u.Id==f.User.Id,Builders<User>.Update.Set(u=>u.Role,"librarian").Set(u=>u.EmailVerifiedAt,null));
  await f.Db.Books.UpdateOneAsync(b=>b.Id==f.Book.Id,Builders<Book>.Update.Set(b=>b.IsActive,false));
  Assert.Equal(404,(await new ReviewQueries(f.Db).PublicAsync(f.Book.Id,new(),default)).Status);
  Assert.Equal(200,(await store.RemoveAsync(f.User.Id,f.Book.Id,new(1),default)).Status);
 });
}
