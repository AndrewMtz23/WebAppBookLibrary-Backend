using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Contracts.Reviews;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Services.Reviews;
namespace WebAppBookLibrary.Controllers;
public abstract class ReviewApiController:ControllerBase
{
 protected string ActorId=>User.FindFirstValue(ClaimTypes.NameIdentifier)??"";
 protected IActionResult Result<T>(ReviewResult<T> result)
 {
  if(result.Status==200)return new JsonResult(result.Value);
  if(result.RetryAfterSeconds.HasValue)Response.Headers.RetryAfter=result.RetryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
  var message=result.Code switch{
   "review_display_name_required"=>"Completa tu nombre visible en el perfil antes de publicar.",
   "review_verified_reader_required"=>"Necesitas una cuenta de lector con correo verificado.",
   "reviews_intake_disabled"=>"La publicación está temporalmente suspendida. Puedes consultar o retirar tu reseña.",
   _=>result.Status switch{409=>"El contenido cambió. Actualiza antes de enviar nuevamente.",429=>"Alcanzaste el límite de envíos. Conserva tu borrador e intenta más tarde.",403=>"No tienes permiso para esta operación.",404=>"La reseña o el libro no están disponibles.",401=>"Inicia sesión con una cuenta activa.",_=>"Revisa los datos ingresados."}};
  var problem=ApiProblemFactory.Create(HttpContext,result.Status,message);problem.Extensions["code"]=result.Code;
  var response=new ObjectResult(problem){StatusCode=result.Status};response.ContentTypes.Add("application/problem+json");return response;
 }
}
[ApiController,Authorize]
public sealed class ReviewsController(MongoReviewStore store,ReviewModerationStore moderation,ReviewQueries queries):ReviewApiController
{
 [AllowAnonymous,HttpGet("api/books/{id}/reviews")] public async Task<IActionResult> Public(string id,[FromQuery]ReviewQuery query,CancellationToken ct)=>Result(await queries.PublicAsync(id,query,ct));
 [HttpGet("api/books/{id}/reviews/my")] public async Task<IActionResult> Mine(string id,CancellationToken ct)=>Result(await queries.MineAsync(ActorId,id,ct));
 [HttpGet("api/reviews/{id}/my")] public async Task<IActionResult> MineById(string id,CancellationToken ct)=>Result(await queries.MineByIdAsync(ActorId,id,ct));
 [HttpPut("api/books/{id}/reviews/my")] public async Task<IActionResult> Save(string id,SaveReview request,CancellationToken ct)=>Result(await store.SaveAsync(ActorId,id,request,ct));
 [HttpDelete("api/books/{id}/reviews/my")] public async Task<IActionResult> Remove(string id,[FromQuery]long expectedVersion,CancellationToken ct)=>Result(await store.RemoveAsync(ActorId,id,new(expectedVersion),ct));
 [HttpPost("api/reviews/{id}/reports")] public async Task<IActionResult> Report(string id,ReportReview request,CancellationToken ct)=>Result(await moderation.ReportAsync(ActorId,id,request,ct));
}
