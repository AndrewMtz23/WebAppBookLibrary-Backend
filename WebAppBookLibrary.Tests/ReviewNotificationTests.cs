using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
namespace WebAppBookLibrary.Tests;
public sealed class ReviewNotificationTests
{
 [LocalMongoFact] public Task Mail_revalidates_existence_and_owner_and_uses_only_generic_content()=>StaffReviewRegressionTests.WithDatabase(async db=>{
  var f=await ReviewPersistenceTests.Seed(db);await db.GetCollection<NotificationPreferences>("NotificationPreferences").InsertOneAsync(new(){Id=f.User.Id,Email=true});
  var review=new Review{UserId=f.User.Id,BookId=f.Book.Id,Text="Private text",ModerationReason="Private reason"};await db.GetCollection<Review>("Reviews").InsertOneAsync(review);
  var n=new Notification{UserId=f.User.Id,TargetType="review",TargetId=review.Id,Title="Update",Body="Sign in"};await db.GetCollection<Notification>("Notifications").InsertOneAsync(n);
  var mail=await NotificationMail.PrepareAsync(f.Db,n.Id,DateTime.UtcNow,"https://example.invalid",new NotificationOptions(),default);Assert.NotNull(mail);Assert.DoesNotContain("Private",mail.Body);
  await db.GetCollection<Review>("Reviews").UpdateOneAsync(r=>r.Id==review.Id,Builders<Review>.Update.Set(r=>r.UserId,"another"));Assert.Null(await NotificationMail.PrepareAsync(f.Db,n.Id,DateTime.UtcNow,"https://example.invalid",new NotificationOptions(),default));
  await db.GetCollection<Review>("Reviews").DeleteOneAsync(r=>r.Id==review.Id);Assert.Null(await NotificationMail.PrepareAsync(f.Db,n.Id,DateTime.UtcNow,"https://example.invalid",new NotificationOptions(),default));
 });
}
