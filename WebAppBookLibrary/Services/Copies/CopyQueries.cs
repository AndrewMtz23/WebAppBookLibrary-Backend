using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Domain.Copies;

namespace WebAppBookLibrary.Services.Copies;

public sealed class CopyQueries(MongoDBService mongo)
{
    private IMongoCollection<BookCopy> Copies=>mongo._database.GetCollection<BookCopy>("BookCopies");
    public async Task<CopyResponse?> GetAsync(string id,CancellationToken ct)
    {
        if(!ObjectId.TryParse(id,out _)) return null;
        var copy=await Copies.Find(c=>c.Id==id).FirstOrDefaultAsync(ct);
        if(copy is null)return null;
        var response=CopyResponse.From(copy);
        if(copy.ActiveAssignmentType=="pickup"&&copy.ActiveAssignmentId is not null)
        {
            var pickup=await mongo.PickupReservations.Find(p=>p.Id==copy.ActiveAssignmentId).FirstOrDefaultAsync(ct);
            var reader=pickup is null?null:await mongo.Users.Find(u=>u.Id==pickup.UserId).FirstOrDefaultAsync(ct);
            response=response with {AssignmentVersion=pickup?.Version,ReaderDisplayName=reader?.DisplayName};
        }
        return response;
    }
    public async Task<CopyResponse?> LookupAsync(string code,CancellationToken ct)
    {
        var normalized=CopyRules.NormalizeCode(code);
        var copy=await Copies.Find(c=>c.InventoryCode==normalized).FirstOrDefaultAsync(ct);
        return copy is null?null:CopyResponse.From(copy);
    }
    public async Task<CopyPage> ListAsync(string bookId,string? status,int page,int pageSize,CancellationToken ct)
    {
        ValidatePage(page,pageSize);
        var filter=Builders<BookCopy>.Filter.Eq(c=>c.BookId,bookId);
        var grouped=await Copies.Aggregate().Match(filter).Group(c=>c.Status,g=>new {Status=g.Key,Count=g.Count()}).ToListAsync(ct);
        var states=grouped.ToDictionary(g=>g.Status,g=>g.Count);
        if(!string.IsNullOrEmpty(status)) filter &= Builders<BookCopy>.Filter.Eq(c=>c.Status,status);
        var count=await Copies.CountDocumentsAsync(filter,cancellationToken:ct);
        var rows=await Copies.Find(filter).SortBy(c=>c.InventoryCode).Skip((page-1)*pageSize).Limit(pageSize).ToListAsync(ct);
        return new(rows.Select(CopyResponse.From).ToArray(),checked((int)count),page,pageSize,states);
    }
    public async Task<CopyHistoryPage> HistoryAsync(string id,int page,int pageSize,CancellationToken ct)
    {
        ValidatePage(page,pageSize);
        var history=mongo._database.GetCollection<CopyHistoryEntry>("CopyHistory");
        var filter=Builders<CopyHistoryEntry>.Filter.Eq(h=>h.CopyId,id);
        var count=await history.CountDocumentsAsync(filter,cancellationToken:ct);
        var rows=await history.Find(filter).SortByDescending(h=>h.CreatedAt).ThenByDescending(h=>h.Id).Skip((page-1)*pageSize).Limit(pageSize).ToListAsync(ct);
        return new(rows.Select(h=>new CopyHistoryResponse(h.Id,h.Action,h.FromStatus,h.ToStatus,h.ActorId,h.Reason,h.CreatedAt)).ToArray(),checked((int)count),page,pageSize);
    }
    private static void ValidatePage(int page,int size)
    { if(page is <1 or >1000000 || size is <1 or >100) throw new ArgumentException("Invalid page."); }
}
