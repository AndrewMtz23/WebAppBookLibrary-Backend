using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Domain.Copies;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Copies;

public sealed class CopyStore(MongoDBService mongo,TimeProvider clock)
{
    private readonly CopyContext context=new(mongo,clock);

    public Task<CopyMutationResult> CreateAsync(string bookId,CreateCopyRequest request,CopyActor actor,CancellationToken ct)
    {
        string code;
        try { code=CopyRules.NormalizeCode(request.InventoryCode); } catch(ArgumentException) { return Task.FromResult(new CopyMutationResult(false,"copy_invalid")); }
        if(!ObjectId.TryParse(bookId,out _) || !CopyRules.ValidCondition(request.Condition) || !CopyRules.ValidNotes(request.Notes)
            || request.Location?.Length>200 || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length>100)
            return Task.FromResult(new CopyMutationResult(false,"copy_invalid"));
        var key=actor.UserId+":"+request.IdempotencyKey;
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{bookId,code,request.Condition,request.Location,request.Notes}))));
        return context.Transact(actor,async(tx,user,token)=>
        {
            var prior=await context.Copies.Find(tx,c=>c.CreationKey==key).FirstOrDefaultAsync(token);
            if(prior is not null) return prior.CreationFingerprint==fingerprint ? new(true,null,prior,true) : new(false,"idempotency_conflict");
            var book=await context.LockBookAsync(tx,bookId,token);
            if(!book.IsActive) throw new CopyConflict("book_inactive");
            if(!await new CopyAllocation(mongo,clock).ConsistentAsync(tx,book,token))
                throw new CopyConflict("inventory_reconciliation_required");
            var copy=new BookCopy { Id=ObjectId.GenerateNewId().ToString(),BookId=bookId,InventoryCode=code,Condition=request.Condition,
                Status=request.Condition=="damaged"?"maintenance":"available",InspectionPending=false,Location=request.Location,Notes=request.Notes,
                CreationKey=key,CreationFingerprint=fingerprint,CreatedAt=context.Now,UpdatedAt=context.Now };
            await context.Copies.InsertOneAsync(tx,copy,cancellationToken:token);
            await context.RefreshProjectionAsync(tx,bookId,token);
            await context.AuditAsync(tx,copy,"created",null,user.Id,null,token);
            return new(true,null,copy);
        },ct);
    }

    public Task<CopyMutationResult> UpdateAsync(string id,UpdateCopyRequest request,CopyActor actor,CancellationToken ct)
    {
        if(!ObjectId.TryParse(id,out _) || request.ExpectedVersion<1 || !CopyRules.ValidNotes(request.Notes) || request.Location?.Length>200)
            return Task.FromResult(new CopyMutationResult(false,"copy_invalid"));
        return context.Transact(actor,async(tx,user,token)=>
        {
            var copy=await context.Copies.Find(tx,c=>c.Id==id).FirstOrDefaultAsync(token);
            if(copy is null) throw new CopyConflict("copy_not_found");
            if(copy.Version!=request.ExpectedVersion) throw new CopyConflict("version_conflict");
            await context.LockBookAsync(tx,copy.BookId,token);
            copy.Location=request.Location;copy.Notes=request.Notes;copy.Version++;copy.UpdatedAt=context.Now;
            await context.Copies.ReplaceOneAsync(tx,c=>c.Id==id,copy,cancellationToken:token);
            await context.AuditAsync(tx,copy,"metadata_updated",copy.Status,user.Id,null,token);
            return new(true,null,copy);
        },ct);
    }
}
