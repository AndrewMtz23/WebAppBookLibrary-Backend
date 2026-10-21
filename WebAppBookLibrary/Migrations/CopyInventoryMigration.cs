using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Copies;

namespace WebAppBookLibrary.Migrations;

public sealed record CopyMigrationPreview(string BookId,bool Eligible,bool AlreadyConverted,int Total,int AvailableBefore,int AvailableAfter,int Held,int Loaned,string Fingerprint,IReadOnlyList<string> Anomalies);
public sealed record CopyMigrationResult(bool Success,string? ErrorCode,bool AlreadyConverted=false);

public sealed class CopyInventoryMigration(MongoDBService mongo,TimeProvider clock)
{
    private readonly CopyContext context=new(mongo,clock);
    private sealed record Snapshot(Book? Book,List<Loan> Loans,List<PickupReservation> Pickups,CopyMigrationPreview Preview);

    public async Task<CopyMigrationPreview> PreviewAsync(string bookId,CancellationToken ct)
    {
        if(!ObjectId.TryParse(bookId,out _))throw new ArgumentException("Invalid book identifier.");
        using var session=await mongo._database.Client.StartSessionAsync(cancellationToken:ct);
        return await session.WithTransactionAsync(async(tx,token)=>(await ReadAsync(tx,bookId,token)).Preview,
            new TransactionOptions(readConcern:ReadConcern.Snapshot),ct);
    }

    public async Task<CopyMigrationResult> ApplyAsync(string bookId,string expectedFingerprint,string operatorId,CancellationToken ct)
    {
        if(!ObjectId.TryParse(bookId,out _)||!ObjectId.TryParse(operatorId,out _))return new(false,"invalid_identifier");
        await CopyIndexes.CreateAsync(mongo._database);
        using var session=await mongo._database.Client.StartSessionAsync(cancellationToken:ct);
        try
        {
            return await session.WithTransactionAsync(async(tx,token)=>
            {
                var actor=await mongo.Users.Find(tx,u=>u.Id==operatorId&&u.IsActive&&u.Role=="admin").FirstOrDefaultAsync(token);
                if(actor is null)return new CopyMigrationResult(false,"copy_forbidden");
                await UserReferenceGuard.TouchAsync(mongo.Users,tx,actor.Id,token);
                var snapshot=await ReadAsync(tx,bookId,token);var preview=snapshot.Preview;
                if(!preview.Eligible)return new CopyMigrationResult(false,"inventory_reconciliation_required");
                if(preview.AlreadyConverted)return new CopyMigrationResult(true,null,true);
                if(preview.Fingerprint!=expectedFingerprint)return new CopyMigrationResult(false,"migration_snapshot_changed");
                await mongo.Books.UpdateOneAsync(tx,b=>b.Id==bookId,Builders<Book>.Update.Inc(b=>b.ReferenceVersion,1).Inc(b=>b.InventoryVersion,1),cancellationToken:token);
                var copies=new List<BookCopy>();
                for(var i=0;i<preview.Total;i++)
                {
                    var code=$"MIG-{bookId}-{i+1:D6}".ToUpperInvariant();
                    var copy=new BookCopy{Id=Hash(code)[..24].ToLowerInvariant(),BookId=bookId,InventoryCode=code,Status="maintenance",InspectionPending=true,
                        CreatedAt=context.Now,UpdatedAt=context.Now,ConversionId=preview.Fingerprint};
                    if(i<snapshot.Loans.Count)
                    {
                        var loan=snapshot.Loans[i];copy.Status="loaned";copy.ActiveAssignmentType="loan";copy.ActiveAssignmentId=loan.Id;
                        await mongo.Loans.UpdateOneAsync(tx,l=>l.Id==loan.Id,Builders<Loan>.Update.Set(l=>l.CopyId,copy.Id).Set(l=>l.CopyInventoryCode,copy.InventoryCode),cancellationToken:token);
                    }
                    else if(i<snapshot.Loans.Count+snapshot.Pickups.Count)
                    {
                        var pickup=snapshot.Pickups[i-snapshot.Loans.Count];copy.Status="held";copy.ActiveAssignmentType="pickup";copy.ActiveAssignmentId=pickup.Id;
                        await mongo.PickupReservations.UpdateOneAsync(tx,p=>p.Id==pickup.Id,Builders<PickupReservation>.Update.Set(p=>p.CopyId,copy.Id).Inc(p=>p.Version,1),cancellationToken:token);
                    }
                    copies.Add(copy);
                }
                if(copies.Count>0)await context.Copies.InsertManyAsync(tx,copies,cancellationToken:token);
                foreach(var copy in copies)await context.AuditAsync(tx,copy,"migrated",null,operatorId,"Pending physical inspection",token);
                await mongo.Books.UpdateOneAsync(tx,b=>b.Id==bookId,Builders<Book>.Update.Set(b=>b.InventoryMode,"unit"),cancellationToken:token);
                await context.RefreshProjectionAsync(tx,bookId,token);
                var converted=await mongo.Books.Find(tx,b=>b.Id==bookId).SingleAsync(token);
                if(!await new CopyAllocation(mongo,clock).ConsistentAsync(tx,converted,token))throw new CopyConflict("inventory_reconciliation_required");
                return new CopyMigrationResult(true,null);
            },new TransactionOptions(readConcern:ReadConcern.Snapshot,writeConcern:WriteConcern.WMajority),ct);
        }
        catch(CopyConflict ex){return new(false,ex.Message);}
        catch(MongoWriteException ex)when(ex.WriteError.Category==ServerErrorCategory.DuplicateKey){return new(false,"copy_duplicate");}
    }

    private async Task<Snapshot> ReadAsync(IClientSessionHandle tx,string bookId,CancellationToken ct)
    {
        var book=await mongo.Books.Find(tx,b=>b.Id==bookId).FirstOrDefaultAsync(ct);
        var loans=(await mongo.Loans.Find(tx,l=>l.BookId==bookId).ToListAsync(ct))
            .Where(l=>LoanResponse.From(l,context.Now).Status is "active" or "overdue").OrderBy(l=>l.ReservedAt==default?l.LoanDate:l.ReservedAt).ThenBy(l=>l.Id,StringComparer.Ordinal).ToList();
        var pickups=await mongo.PickupReservations.Find(tx,p=>p.BookId==bookId&&p.Status=="ready").SortBy(p=>p.ReadyAt).ThenBy(p=>p.Id).ToListAsync(ct);
        var anomalies=new List<string>();var converted=book?.InventoryMode=="unit";
        if(book is null||book.MediaType!="physical")anomalies.Add("physical_book_required");
        if(book is not null&&converted)
        {
            if(!await new CopyAllocation(mongo,clock).ConsistentAsync(tx,book,ct))anomalies.Add("unit_inventory_inconsistent");
        }
        else
        {
            if(book?.TotalCopies is null or <0 || book.AvailableCopies is null or <0 || book.RetainedCopies is null or <0)anomalies.Add("counts_missing_or_negative");
            if(book?.TotalCopies>500)anomalies.Add("conversion_limit_500_exceeded");
            if(book?.AvailableCopies+loans.Count+pickups.Count!=book?.TotalCopies ||book?.RetainedCopies!=pickups.Count)anomalies.Add("counts_do_not_reconcile");
            if(await context.Copies.Find(tx,c=>c.BookId==bookId).AnyAsync(ct)||loans.Any(l=>l.CopyId!=null)||pickups.Any(p=>p.CopyId!=null))anomalies.Add("existing_unit_references");
            foreach(var userId in loans.Select(l=>l.UserId).Concat(pickups.Select(p=>p.UserId)).Distinct())
                if(!ObjectId.TryParse(userId,out _)||!await mongo.Users.Find(tx,u=>u.Id==userId).AnyAsync(ct))anomalies.Add("missing_account_reference");
            if(loans.Any(l=>l.MediaType=="digital"))anomalies.Add("digital_loan_on_physical_book");
        }
        var fingerprint=Hash(JsonSerializer.Serialize(new{book,loans,pickups}));
        var preview=new CopyMigrationPreview(bookId,anomalies.Count==0,converted,book?.TotalCopies??0,book?.AvailableCopies??0,converted?book?.AvailableCopies??0:0,pickups.Count,loans.Count,fingerprint,anomalies);
        return new(book,loans,pickups,preview);
    }
    private static string Hash(string input)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
