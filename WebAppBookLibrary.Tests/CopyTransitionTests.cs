using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Copies;

namespace WebAppBookLibrary.Tests;
public sealed class CopyTransitionTests
{
    [LocalMongoFact]
    public Task Inspection_and_admin_retirement_have_explicit_inventory_effects()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await CopyPersistenceTests.Seed(db);
        var copy=(await new CopyStore(f.Db,TimeProvider.System).CreateAsync(f.Book.Id,new("STATE-1","damaged",null,null,"one"),f.Actor,default)).Copy!;
        var transitions=new CopyTransitions(f.Db,TimeProvider.System);
        Assert.False((await transitions.ChangeStatusAsync(copy.Id,new("available","Repaired",1,"release"),f.Actor,default)).Success);
        var inspected=await transitions.InspectAsync(copy.Id,1,new("good","A1",null),f.Actor,default);
        Assert.True(inspected.Success);Assert.Equal("available",inspected.Copy!.Status);
        Assert.Equal(1,(await f.Db.Books.Find(b=>b.Id==f.Book.Id).SingleAsync()).AvailableCopies);
        Assert.Equal("copy_forbidden",(await transitions.ChangeStatusAsync(copy.Id,new("retired","Withdrawn",2,"retire"),f.Actor,default)).ErrorCode);
        var admin=await SupportPersistenceTests.UserAsync(f.Db,"admin");
        var retired=await transitions.ChangeStatusAsync(copy.Id,new("retired","Withdrawn",2,"retire"),new(admin.Id,true),default);
        Assert.True(retired.Success);Assert.Equal("retired",retired.Copy!.Status);
        Assert.Equal(0,(await f.Db.Books.Find(b=>b.Id==f.Book.Id).SingleAsync()).TotalCopies);
        Assert.False((await transitions.InspectAsync(copy.Id,3,new("good",null,null),new(admin.Id,true),default)).Success);
    });

    [LocalMongoFact]
    public Task Lost_loan_is_closed_and_recovery_never_reopens_it()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await CopyPersistenceTests.Seed(db);var reader=await SupportPersistenceTests.UserAsync(f.Db,"user");
        var copy=(await new CopyStore(f.Db,TimeProvider.System).CreateAsync(f.Book.Id,new("STATE-2","good",null,null,"one"),f.Actor,default)).Copy!;
        var circulation=new MongoCirculationStore(f.Db,Microsoft.Extensions.Options.Options.Create(new WebAppBookLibrary.Configuration.CirculationOptions{Mode="active"}));
        var p=(await circulation.ReserveForPickupAsync(reader.Id,f.Book.Id,"reserve",default)).Reservation!;
        var loan=(await circulation.CollectPickupReservationAsync(p.Id,f.Actor.UserId,1,default,new(copy.Id,2))).Loan!;
        var renewal=new RenewalRequest{Id=MongoDB.Bson.ObjectId.GenerateNewId().ToString(),LoanId=loan.Id,BookId=f.Book.Id,UserId=reader.Id};
        await f.Db.RenewalRequests.InsertOneAsync(renewal);
        var transitions=new CopyTransitions(f.Db,TimeProvider.System,Microsoft.Extensions.Options.Options.Create(new WebAppBookLibrary.Configuration.NotificationOptions{Enabled=false}));
        var lost=await transitions.ChangeStatusAsync(copy.Id,new("lost","Not recovered",3,"lost"),f.Actor,default);
        Assert.True(lost.Success);Assert.Null(lost.Copy!.ActiveAssignmentId);
        Assert.False(await db.GetCollection<Notification>("Notifications").Find(n=>n.Type=="copy_unavailable").AnyAsync());
        Assert.Equal("cancelled",(await f.Db.RenewalRequests.Find(r=>r.Id==renewal.Id).SingleAsync()).Status);
        Assert.True(await f.Db.CirculationHistory.Find(h=>h.EntityType=="renewal_request"&&h.EntityId==renewal.Id&&h.ToStatus=="cancelled").AnyAsync());
        var closed=await f.Db.Loans.Find(l=>l.Id==loan.Id).SingleAsync();Assert.Equal("lost",closed.Status);Assert.Null(closed.ReturnedAt);
        Assert.Equal("lost",new WebAppBookLibrary.Contracts.Loans.LoanQuery{Status="lost"}.Normalize().Status);
        Assert.False(await circulation.ReturnPhysicalLoanAsync(loan.Id,f.Actor.UserId,default,request:new(new(copy.Id,3),new("good",null,null),"return")));
        var recovered=await transitions.ChangeStatusAsync(copy.Id,new("maintenance","Found",4,"found"),f.Actor,default);
        Assert.True(recovered.Success);Assert.True(recovered.Copy!.InspectionPending);
        Assert.Equal("lost",(await f.Db.Loans.Find(l=>l.Id==loan.Id).SingleAsync()).Status);
    });
}
