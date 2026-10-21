using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Copies;

namespace WebAppBookLibrary.Controllers;

[ApiController, Authorize(Policy=PolicyNames.ManageCopies)]
public sealed class CopiesController(CopyStore store,CopyQueries queries,CopyTransitions transitions,MongoDBService mongo):ControllerBase
{
    private CopyActor Actor=>new(User.FindFirstValue(ClaimTypes.NameIdentifier)??"",User.IsInRole("admin"));
    private Task<bool> Active(CancellationToken ct)=>mongo.Users.Find(u=>u.Id==Actor.UserId&&u.IsActive&&(u.Role=="admin"||u.Role=="librarian")).AnyAsync(ct);
    private IActionResult Error(string code,int status)
    {
        var problem=new ProblemDetails{Status=status,Title=code};problem.Extensions["code"]=code;
        return new ObjectResult(problem){StatusCode=status,ContentTypes={"application/problem+json"}};
    }
    private IActionResult Result(CopyMutationResult result)=>result.Success?Ok(CopyResponse.From(result.Copy!)):Error(result.ErrorCode!,result.ErrorCode switch{
        "copy_forbidden"=>403,"copy_not_found" or "book_not_found"=>404,"copy_invalid"=>400,_=>409});
    private static bool Page(int page,int size)=>page is >=1 and <=1000000 && size is >=1 and <=100;

    [HttpGet("api/books/{bookId}/copies")]
    public async Task<IActionResult> List(string bookId,string? status=null,int page=1,int pageSize=20,CancellationToken ct=default)
    {
        if(!await Active(ct))return Error("copy_forbidden",403);
        if(!ObjectId.TryParse(bookId,out _)||!Page(page,pageSize)||status is not (null or "available" or "held" or "loaned" or "maintenance" or "lost" or "retired"))return Error("copy_invalid",400);
        return Ok(await queries.ListAsync(bookId,status,page,pageSize,ct));
    }
    [HttpPost("api/books/{bookId}/copies")]
    public async Task<IActionResult> Create(string bookId,CreateCopyRequest request,CancellationToken ct)=>Result(await store.CreateAsync(bookId,request,Actor,ct));
    [HttpGet("api/copies/lookup")]
    public async Task<IActionResult> Lookup(string code,CancellationToken ct)
    {
        if(!await Active(ct))return Error("copy_forbidden",403);
        try { var copy=await queries.LookupAsync(code,ct);return copy is null?Error("copy_not_found",404):Ok(copy); }
        catch(ArgumentException){return Error("copy_invalid",400);}
    }
    [HttpGet("api/copies/{id}")]
    public async Task<IActionResult> Detail(string id,CancellationToken ct)
    {
        if(!await Active(ct))return Error("copy_forbidden",403);
        if(!ObjectId.TryParse(id,out _))return Error("copy_invalid",400);
        var copy=await queries.GetAsync(id,ct);return copy is null?Error("copy_not_found",404):Ok(copy);
    }
    [HttpPut("api/copies/{id}")]
    public async Task<IActionResult> Update(string id,UpdateCopyRequest request,CancellationToken ct)=>Result(await store.UpdateAsync(id,request,Actor,ct));
    [HttpPut("api/copies/{id}/status")]
    public async Task<IActionResult> ChangeStatus(string id,ChangeCopyStatusRequest request,CancellationToken ct)=>Result(await transitions.ChangeStatusAsync(id,request,Actor,ct));
    [HttpPut("api/copies/{id}/inspection")]
    public async Task<IActionResult> Inspect(string id,InspectCopyRequest request,CancellationToken ct)=>Result(await transitions.InspectAsync(id,request.ExpectedVersion,request.Inspection,Actor,ct));
    [HttpGet("api/copies/{id}/history")]
    public async Task<IActionResult> History(string id,int page=1,int pageSize=20,CancellationToken ct=default)
    {
        if(!await Active(ct))return Error("copy_forbidden",403);
        if(!ObjectId.TryParse(id,out _)||!Page(page,pageSize))return Error("copy_invalid",400);
        if(await queries.GetAsync(id,ct) is null)return Error("copy_not_found",404);
        return Ok(await queries.HistoryAsync(id,page,pageSize,ct));
    }
}
