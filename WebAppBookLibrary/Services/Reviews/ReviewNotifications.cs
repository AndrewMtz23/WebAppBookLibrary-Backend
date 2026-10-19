using MongoDB.Driver;
using WebAppBookLibrary.Models;
namespace WebAppBookLibrary.Services.Reviews;
public static class ReviewNotifications
{
 public static Task AppendAsync(IMongoDatabase db,IClientSessionHandle tx,Review review,DateTime now,CancellationToken ct)=>
 NotificationEvents.AppendEventAsync(db,tx,review.UserId,$"review:{review.Id}:{review.Version}","review_update","Actualización de tu reseña","Hay una actualización sobre tu reseña. Inicia sesión para consultarla.",review.Id,null,null,now,ct,"review");
}
