using MongoDB.Driver;
using WebAppBookLibrary.Domain.Copies;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Copies;

internal sealed class CopyAllocation(MongoDBService mongo,TimeProvider clock)
{
    private readonly CopyContext context=new(mongo,clock);

    internal async Task<BookCopy?> HoldAsync(IClientSessionHandle tx,string bookId,string pickupId,CancellationToken ct)
    {
        var book=await mongo.Books.Find(tx,b=>b.Id==bookId).FirstOrDefaultAsync(ct);
        if(book?.InventoryMode!="unit")return null;
        await context.LockBookAsync(tx,bookId,ct);
        var copy=await context.Copies.Find(tx,c=>c.BookId==bookId&&c.Status=="available"&&!c.InspectionPending&&(c.Condition=="good"||c.Condition=="worn")&&c.ActiveAssignmentId==null)
            .SortBy(c=>c.InventoryCode).FirstOrDefaultAsync(ct);
        if(copy is null)throw new CopyConflict("inventory_reconciliation_required");
        copy.Status="held";copy.ActiveAssignmentType="pickup";copy.ActiveAssignmentId=pickupId;copy.Version++;copy.UpdatedAt=context.Now;
        await context.Copies.ReplaceOneAsync(tx,c=>c.Id==copy.Id,copy,cancellationToken:ct);
        await context.AuditAsync(tx,copy,"held","available",null,null,ct);
        return copy;
    }

    internal async Task<BookCopy> ReleaseAsync(IClientSessionHandle tx,string copyId,CancellationToken ct)
    {
        var copy=await context.Copies.Find(tx,c=>c.Id==copyId).FirstOrDefaultAsync(ct);
        if(copy is null)throw new CopyConflict("inventory_reconciliation_required");
        await context.LockBookAsync(tx,copy.BookId,ct);
        var from=copy.Status;copy.Status="available";copy.ActiveAssignmentType=null;copy.ActiveAssignmentId=null;
        if(!CopyRules.IsLendable(copy))copy.Status="maintenance";
        copy.Version++;copy.UpdatedAt=context.Now;
        await context.Copies.ReplaceOneAsync(tx,c=>c.Id==copyId,copy,cancellationToken:ct);
        await context.AuditAsync(tx,copy,"released",from,null,null,ct);
        return copy;
    }

    internal async Task<bool> ConsistentAsync(IClientSessionHandle tx,Book book,CancellationToken ct)
    {
        var copies=await context.Copies.Find(tx,c=>c.BookId==book.Id).ToListAsync(ct);
        if(copies.Any(c=>c.Status=="available"&&!CopyRules.IsLendable(c)))return false;
        if(book.TotalCopies!=copies.Count(c=>c.Status!="retired")||book.AvailableCopies!=copies.Count(CopyRules.IsLendable)||book.RetainedCopies!=copies.Count(c=>c.Status=="held"))return false;
        var pickups=await mongo.PickupReservations.Find(tx,p=>p.BookId==book.Id&&p.Status=="ready").ToListAsync(ct);
        var loans=await mongo.Loans.Find(tx,l=>l.BookId==book.Id&&(l.Status=="active"||l.Status=="overdue"||((l.Status==""||l.Status==null)&&!l.IsReturned))).ToListAsync(ct);
        return pickups.Count==copies.Count(c=>c.Status=="held")&&loans.Count==copies.Count(c=>c.Status=="loaned")
            &&pickups.All(p=>copies.Any(c=>c.Id==p.CopyId&&c.Status=="held"&&c.ActiveAssignmentType=="pickup"&&c.ActiveAssignmentId==p.Id))
            &&loans.All(l=>copies.Any(c=>c.Id==l.CopyId&&c.Status=="loaned"&&c.ActiveAssignmentType=="loan"&&c.ActiveAssignmentId==l.Id));
    }
}
