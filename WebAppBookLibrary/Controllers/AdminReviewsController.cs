using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Contracts.Reviews;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services.Reviews;
namespace WebAppBookLibrary.Controllers;
[ApiController,Authorize(Policy=PolicyNames.ManageReviews)]
public sealed class AdminReviewsController(ReviewModerationStore store,ReviewQueries queries):ReviewApiController
{
 [HttpGet("api/admin/reviews")] public async Task<IActionResult> List([FromQuery]ReviewQuery query,CancellationToken ct)=>Result(await queries.AdminListAsync(ActorId,query,ct));
 [HttpGet("api/admin/reviews/{id}")] public async Task<IActionResult> Detail(string id,int page=1,int pageSize=20,CancellationToken ct=default)=>Result(await queries.AdminDetailAsync(ActorId,id,page,pageSize,ct));
 [HttpGet("api/admin/review-reports")] public async Task<IActionResult> Reports([FromQuery]ReviewReportQuery query,CancellationToken ct)=>Result(await queries.ReportsAsync(ActorId,query,ct));
 [HttpPut("api/admin/reviews/{id}/visibility")] public async Task<IActionResult> Visibility(string id,SetReviewVisibility request,CancellationToken ct)=>Result(await store.SetVisibilityAsync(ActorId,id,request,ct));
 [HttpPut("api/admin/review-reports/{id}/resolution")] public async Task<IActionResult> Resolve(string id,ResolveReviewReport request,CancellationToken ct)=>Result(await store.ResolveAsync(ActorId,id,request,ct));
}
