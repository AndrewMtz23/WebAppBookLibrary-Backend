using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using WebAppBookLibrary.Contracts.Categories;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;
namespace WebAppBookLibrary.Controllers;
[ApiController, Route("api/categories")]
public sealed class CategoriesController(CategoryService categories) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get([FromQuery(Name = "")] CategoryQuery query, CancellationToken ct) => Ok(await categories.SearchAsync(query, false, ct));
}
[ApiController, Route("api/admin/categories"), Authorize(Policy = PolicyNames.ManageCategories)]
public sealed class AdminCategoriesController(CategoryService categories, Logservice audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery(Name = "")] CategoryQuery query, CancellationToken ct) => Ok(await categories.SearchAsync(query, true, ct));
    [HttpPost]
    public async Task<IActionResult> Create(CategoryWriteRequest request, CancellationToken ct) => await Respond(await categories.CreateAsync(request, ct), "created", "", true);
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, CategoryWriteRequest request, CancellationToken ct) => !ObjectId.TryParse(id, out _) ? ProblemCode(400, "category_id_invalid") : await Respond(await categories.UpdateAsync(id, request, ct), "updated", id);
    [HttpPatch("{id}/status")]
    public async Task<IActionResult> Status(string id, CategoryStatusRequest request, CancellationToken ct) => !ObjectId.TryParse(id, out _) ? ProblemCode(400, "category_id_invalid") : await Respond(await categories.StatusAsync(id, request, ct), request.IsActive ? "activated" : "deactivated", id);
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] long version, CancellationToken ct) => !ObjectId.TryParse(id, out _) ? ProblemCode(400, "category_id_invalid") : await Respond(await categories.DeleteAsync(id, version, ct), "deleted", id);
    private async Task<IActionResult> Respond(CategoryMutationResult result, string action, string id, bool created = false)
    {
        await audit.LogDomainAsync(AuditLogEntryFactory.CategoryChanged(action, User.Identity?.Name ?? "", result.Category?.Id ?? id,
            new Dictionary<string, string> { ["result"] = result.Success ? "success" : "failed", ["reasonCode"] = result.ErrorCode }, HttpContext));
        if (!result.Success) return ProblemCode(result.ErrorCode == "category_not_found" ? 404 : result.ErrorCode == "category_version_required" ? 400 : 409, result.ErrorCode);
        return created ? Created($"/api/admin/categories/{result.Category!.Id}", result.Category) : result.Category is null ? NoContent() : Ok(result.Category);
    }
    private ObjectResult ProblemCode(int status, string code)
    {
        var problem = ApiProblemFactory.Create(HttpContext, status, code);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
