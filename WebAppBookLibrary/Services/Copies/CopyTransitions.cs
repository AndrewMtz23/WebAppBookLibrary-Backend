using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Domain.Copies;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services.Copies;

public sealed class CopyTransitions(MongoDBService mongo,TimeProvider clock,IOptions<NotificationOptions>? notifications=null)
{
    private readonly CopyContext context=new(mongo,clock);

    public Task<CopyMutationResult> ChangeStatusAsync(string copyId,ChangeCopyStatusRequest request,CopyActor actor,CancellationToken ct)
    {
        if(!ObjectId.TryParse(copyId,out _) || string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length>1000
            ||request.ExpectedVersion<1||string.IsNullOrWhiteSpace(request.IdempotencyKey)||request.IdempotencyKey.Length>100)
            return Task.FromResult(new CopyMutationResult(false,"copy_invalid"));
        var key=actor.UserId+":"+request.IdempotencyKey;
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{copyId,request}))));
        return context.Transact(actor,async(tx,user,token)=>
        {
            var prior=await context.History.Find(tx,h=>h.OperationKey==key).FirstOrDefaultAsync(token);
            if(prior is not null)
            {
                if(prior.OperationFingerprint!=fingerprint)throw new CopyConflict("idempotency_conflict");
                return new(true,null,await context.Copies.Find(tx,c=>c.Id==copyId).SingleAsync(token),true);
            }
            var copy=await ReadAsync(tx,copyId,request.ExpectedVersion,token);
            if(request.Status=="retired"&&user.Role!="admin")throw new CopyConflict("copy_forbidden");
            var resolvesLoss=request.Status=="lost"&&copy.Status is "held" or "loaned";
            if(!resolvesLoss&&!CopyRules.CanTransition(copy.Status,request.Status,user.Role=="admin"))throw new CopyConflict("copy_transition_invalid");
            // Availability is granted by an inspection command, not a status-only edit.
            if(request.Status=="available")throw new CopyConflict("copy_inspection_required");
            if(resolvesLoss)await CloseCommitmentAsync(tx,copy,user.Id,request.Reason,token);
            var from=copy.Status;copy.Status=request.Status;
            if(from=="lost"&&request.Status=="maintenance")copy.InspectionPending=true;
            if(request.Status=="retired")copy.RetirementReason=request.Reason;
            await SaveAsync(tx,copy,token);
            await context.History.InsertOneAsync(tx,new CopyHistoryEntry{Id=ObjectId.GenerateNewId().ToString(),CopyId=copy.Id,BookId=copy.BookId,
                Action="status_changed",FromStatus=from,ToStatus=copy.Status,ActorId=user.Id,Reason=request.Reason,CreatedAt=context.Now,
                OperationKey=key,OperationFingerprint=fingerprint},cancellationToken:token);
            return new(true,null,copy);
        },ct);
    }

    public Task<CopyMutationResult> InspectAsync(string copyId,long expectedVersion,CopyInspection inspection,CopyActor actor,CancellationToken ct)
    {
        if(!ObjectId.TryParse(copyId,out _)||expectedVersion<1||!CopyRules.ValidCondition(inspection.Condition)||!CopyRules.ValidNotes(inspection.Notes)||inspection.Location?.Length>200)
            return Task.FromResult(new CopyMutationResult(false,"copy_invalid"));
        return context.Transact(actor,async(tx,user,token)=>
        {
            var copy=await ReadAsync(tx,copyId,expectedVersion,token);
            if(copy.Status is "loaned" or "lost" or "retired")throw new CopyConflict("copy_transition_invalid");
            var from=copy.Status;
            if(copy.Status=="held"&&inspection.Condition=="damaged")await CloseCommitmentAsync(tx,copy,user.Id,"Damaged during inspection",token);
            copy.Condition=inspection.Condition;copy.InspectionPending=false;copy.Location=inspection.Location;copy.Notes=inspection.Notes;
            if(copy.Status!="held"||inspection.Condition=="damaged")copy.Status=inspection.Condition=="damaged"?"maintenance":"available";
            await SaveAsync(tx,copy,token);
            await context.AuditAsync(tx,copy,"inspected",from,user.Id,null,token);
            return new(true,null,copy);
        },ct);
    }

    private async Task<BookCopy> ReadAsync(IClientSessionHandle tx,string id,long version,CancellationToken ct)
    {
        var copy=await context.Copies.Find(tx,c=>c.Id==id).FirstOrDefaultAsync(ct);
        if(copy is null)throw new CopyConflict("copy_not_found");
        if(copy.Version!=version)throw new CopyConflict("version_conflict");
        var book=await context.LockBookAsync(tx,copy.BookId,ct);
        if(!await new CopyAllocation(mongo,clock).ConsistentAsync(tx,book,ct))
            throw new CopyConflict("inventory_reconciliation_required");
        return copy;
    }
    private async Task SaveAsync(IClientSessionHandle tx,BookCopy copy,CancellationToken ct)
    {
        copy.Version++;copy.UpdatedAt=context.Now;
        await context.Copies.ReplaceOneAsync(tx,c=>c.Id==copy.Id,copy,cancellationToken:ct);
        await context.RefreshProjectionAsync(tx,copy.BookId,ct);
    }
    private async Task CloseCommitmentAsync(IClientSessionHandle tx,BookCopy copy,string actor,string reason,CancellationToken ct)
    {
        string? reader;
        if(copy.Status=="held")
        {
            var pickup=await mongo.PickupReservations.Find(tx,p=>p.Id==copy.ActiveAssignmentId&&p.CopyId==copy.Id&&p.Status=="ready").FirstOrDefaultAsync(ct);
            if(pickup is null)throw new CopyConflict("inventory_reconciliation_required");
            reader=pickup.UserId;
            await mongo.PickupReservations.UpdateOneAsync(tx,p=>p.Id==pickup.Id,Builders<PickupReservation>.Update.Set(p=>p.Status,"cancelled").Set(p=>p.CancelledAt,context.Now).Set(p=>p.CancelledReason,reason).Inc(p=>p.Version,1),cancellationToken:ct);
            if(pickup.WaitlistEntryId is not null)await mongo.WaitlistEntries.UpdateOneAsync(tx,w=>w.Id==pickup.WaitlistEntryId,Builders<WaitlistEntry>.Update.Set(w=>w.Status,"cancelled").Set(w=>w.ClosedReason,reason).Inc(w=>w.Version,1),cancellationToken:ct);
        }
        else
        {
            var loan=await mongo.Loans.Find(tx,l=>l.Id==copy.ActiveAssignmentId&&l.CopyId==copy.Id&&(l.Status=="active"||l.Status=="overdue"||((l.Status==""||l.Status==null)&&!l.IsReturned))).FirstOrDefaultAsync(ct);
            if(loan is null)throw new CopyConflict("inventory_reconciliation_required");
            reader=loan.UserId;
            await mongo.Loans.UpdateOneAsync(tx,l=>l.Id==loan.Id,Builders<Loan>.Update.Set(l=>l.Status,"lost").Set(l=>l.LostAt,context.Now).Set(l=>l.LostReason,reason).Set(l=>l.ActiveReservationKey,null),cancellationToken:ct);
            var renewals=await mongo.RenewalRequests.Find(tx,r=>r.LoanId==loan.Id&&r.Status=="pending").ToListAsync(ct);
            await mongo.RenewalRequests.UpdateManyAsync(tx,r=>r.LoanId==loan.Id&&r.Status=="pending",Builders<RenewalRequest>.Update.Set(r=>r.Status,"cancelled").Set(r=>r.DecidedAt,context.Now).Set(r=>r.DecidedByUserId,actor).Set(r=>r.DecisionReason,"Loan lost").Inc(r=>r.Version,1),cancellationToken:ct);
            foreach(var renewal in renewals)
                await mongo.CirculationHistory.InsertOneAsync(tx,new CirculationHistoryEntry{
                    EntityType="renewal_request",EntityId=renewal.Id,BookId=copy.BookId,UserId=reader,
                    FromStatus="pending",ToStatus="cancelled",ActorId=actor,ActorRole="staff",
                    Reason="Loan lost",Timestamp=context.Now},cancellationToken:ct);
        }
        await mongo.Users.UpdateOneAsync(tx,u=>u.Id==reader,Builders<User>.Update.Inc(u=>u.ReferenceVersion,1),cancellationToken:ct);
        await mongo.CirculationHistory.InsertOneAsync(tx,new CirculationHistoryEntry{EntityType=copy.Status=="held"?"pickup_reservation":"loan",EntityId=copy.ActiveAssignmentId!,BookId=copy.BookId,UserId=reader,
            FromStatus=copy.Status=="held"?"ready":"active",ToStatus=copy.Status=="held"?"cancelled":"lost",ActorId=actor,ActorRole="staff",Reason=reason,Timestamp=context.Now},cancellationToken:ct);
        if(notifications?.Value.Enabled ?? true)
            await NotificationEvents.AppendEventAsync(mongo._database,tx,reader,$"copy:{copy.Id}:{copy.Version}:unavailable","copy_unavailable","Ejemplar no disponible","El personal actualizó tu operación porque el ejemplar no está disponible.",copy.BookId,null,null,context.Now,ct);
        copy.ActiveAssignmentId=null;copy.ActiveAssignmentType=null;
    }
}
