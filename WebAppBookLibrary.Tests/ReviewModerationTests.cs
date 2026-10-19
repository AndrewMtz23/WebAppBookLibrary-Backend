using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services.Reviews;
namespace WebAppBookLibrary.Tests;
public sealed class ReviewModerationTests
{
 [LocalMongoFact] public Task Moderation_survives_edits_removal_and_restore_and_notifies_only_author()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var o=Options.Create(new ReviewOptions());var store=new MongoReviewStore(f.Db,o,TimeProvider.System);var mod=new ReviewModerationStore(f.Db,o,TimeProvider.System);var q=new ReviewQueries(f.Db);
  var admin=await SupportPersistenceTests.UserAsync(f.Db,"admin");var librarian=await SupportPersistenceTests.UserAsync(f.Db,"librarian");
  var review=(await store.SaveAsync(f.User.Id,f.Book.Id,new(5,"Private content",0),default)).Value!;
  Assert.Equal(403,(await mod.SetVisibilityAsync(librarian.Id,review.Id,new("hidden","Reason",1),default)).Status);
  Assert.Equal(200,(await mod.SetVisibilityAsync(admin.Id,review.Id,new("hidden","Private reason",1),default)).Status);
  Assert.Equal(409,(await mod.SetVisibilityAsync(admin.Id,review.Id,new("hidden","Private reason",1),default)).Status);
  Assert.Equal(0,(await q.PublicAsync(f.Book.Id,new(),default)).Value!.Total);
  var edit=(await store.SaveAsync(f.User.Id,f.Book.Id,new(3,"Edited",2),default)).Value!;Assert.Equal("hidden",edit.Visibility);
  Assert.Equal(200,(await store.RemoveAsync(f.User.Id,f.Book.Id,new(3),default)).Status);
  var again=(await store.SaveAsync(f.User.Id,f.Book.Id,new(4,"Again",4),default)).Value!;Assert.Equal("hidden",again.Visibility);
  await store.RemoveAsync(f.User.Id,f.Book.Id,new(5),default);
  Assert.Equal(200,(await mod.SetVisibilityAsync(admin.Id,review.Id,new("visible","Reconsidered",6),default)).Status);
  var own=(await q.MineAsync(f.User.Id,f.Book.Id,default)).Value!;Assert.Equal("removed",own.Visibility);Assert.Null(own.Rating);Assert.Equal("",own.Text);
  var notices=await db.GetCollection<Notification>("Notifications").Find(n=>n.TargetType=="review").ToListAsync();Assert.Equal(2,notices.Count);Assert.All(notices,n=>Assert.Equal(f.User.Id,n.UserId));
  var json=System.Text.Json.JsonSerializer.Serialize(notices);Assert.DoesNotContain("Private reason",json);Assert.DoesNotContain("Private content",json);
 });
 [LocalMongoFact] public Task Reports_deduplicate_do_not_hide_and_require_independent_resolution()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);var o=Options.Create(new ReviewOptions{ReportsPerHour=1});var store=new MongoReviewStore(f.Db,o,TimeProvider.System);var mod=new ReviewModerationStore(f.Db,o,TimeProvider.System);
  var reader=await SupportPersistenceTests.UserAsync(f.Db,"user");await f.Db.Users.UpdateOneAsync(u=>u.Id==reader.Id,Builders<User>.Update.Set(u=>u.EmailVerifiedAt,DateTime.UtcNow));
  var admin=await SupportPersistenceTests.UserAsync(f.Db,"admin");var r=(await store.SaveAsync(f.User.Id,f.Book.Id,new(4,"Text",0),default)).Value!;
  Assert.Equal(403,(await mod.ReportAsync(f.User.Id,r.Id,new("Own"),default)).Status);
  var reports=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>mod.ReportAsync(reader.Id,r.Id,new("Spam"),default)));Assert.All(reports,x=>Assert.Equal(200,x.Status));Assert.Equal(reports[0].Value,reports[1].Value);
  Assert.Equal(1,(await new ReviewQueries(f.Db).PublicAsync(f.Book.Id,new(),default)).Value!.Total);
  var reportId=reports[0].Value!;Assert.Equal(200,(await mod.ResolveAsync(admin.Id,reportId,new("dismissed","Reviewed",1),default)).Status);
  Assert.Equal(409,(await mod.ResolveAsync(admin.Id,reportId,new("action_taken","Changed",1),default)).Status);
  Assert.Equal(reportId,(await mod.ReportAsync(reader.Id,r.Id,new("Again"),default)).Value);
  Assert.Equal(403,(await new ReviewQueries(f.Db).ReportsAsync(reader.Id,new(),default)).Status);
 });
}
