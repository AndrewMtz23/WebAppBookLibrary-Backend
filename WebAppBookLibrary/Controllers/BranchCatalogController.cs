using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MongoDB.Bson;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Controllers;

[ApiController, Route("api/branches/{branchId}/catalog"), Authorize(Roles = "admin,librarian")]
public sealed class BranchCatalogController(MongoDBService mongo, BranchCatalogStore store) : ControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    public sealed record MembershipChange(bool IsActive, long ExpectedVersion);

    [HttpGet]
    public async Task<IActionResult> Search(string branchId, string? search = null, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (await new BranchAccessService(mongo).ResolveAsync(Actor, branchId, ct) is null) throw new BranchAccessException("branch_forbidden");
        if (page < 1 || page > 10000 || pageSize < 1 || pageSize > 100 || search?.Length > 200) return BadRequest();
        var filter = Builders<Book>.Filter.Eq(b => b.IsActive, true);
        if (!string.IsNullOrWhiteSpace(search)) filter &= Builders<Book>.Filter.Regex(b => b.Title, new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(search.Trim()), "i")) |
            Builders<Book>.Filter.Eq(b => b.Isbn, WebAppBookLibrary.Domain.Books.BookRules.NormalizeIsbn(search));
        var total = await mongo.Books.CountDocumentsAsync(filter, cancellationToken: ct);
        var books = await mongo.Books.Find(filter).SortBy(b => b.Title).ThenBy(b => b.Id).Skip((page - 1) * pageSize).Limit(pageSize)
            .Project(b => new { b.Id, b.Title, b.Author, b.Isbn, b.MediaType }).ToListAsync(ct);
        var ids = books.Select(b => b.Id).ToArray();
        var entries = await mongo._database.GetCollection<BranchCatalogEntry>("BranchCatalog").Find(e => e.BranchId == branchId && ids.Contains(e.BookId)).ToListAsync(ct);
        return Ok(new { items = books.Select(b => new { book = b, membership = entries.SingleOrDefault(e => e.BookId == b.Id) }), totalItems = total, page, pageSize });
    }

    [HttpPut("{bookId}")]
    public async Task<IActionResult> Set(string branchId, string bookId, MembershipChange request, CancellationToken ct)
    {
        var result = await store.SetAsync(Actor, branchId, bookId, request.IsActive, request.ExpectedVersion, ct);
        if (!result.Success) throw new BranchAccessException(result.ErrorCode!);
        return Ok(result.Value);
    }
}
