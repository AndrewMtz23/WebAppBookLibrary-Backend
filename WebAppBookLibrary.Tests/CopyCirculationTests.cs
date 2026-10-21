using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Copies;

namespace WebAppBookLibrary.Tests;
public sealed class CopyCirculationTests
{
    [LocalMongoFact]
    public Task Two_collectors_receive_one_loan_and_wrong_unit_cannot_be_substituted()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await CopyPersistenceTests.Seed(db);
        var copy=(await new CopyStore(f.Db,TimeProvider.System).CreateAsync(f.Book.Id,new("CIRC-002","good",null,null,"one"),f.Actor,default)).Copy!;
        var reader=await SupportPersistenceTests.UserAsync(f.Db,"user");
        var store=new MongoCirculationStore(f.Db,Options.Create(new CirculationOptions{Mode="active"}));
        var reservation=(await store.ReserveForPickupAsync(reader.Id,f.Book.Id,"reserve",default)).Reservation!;
        Assert.False((await store.CollectPickupReservationAsync(reservation.Id,f.Actor.UserId,1,default)).Success);
        var selection=new WebAppBookLibrary.Contracts.Copies.CopySelection(copy.Id,2);
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>store.CollectPickupReservationAsync(reservation.Id,f.Actor.UserId,1,default,selection)));
        Assert.All(results,r=>Assert.True(r.Success));Assert.Single(results.Select(r=>r.Loan!.Id).Distinct());
        Assert.Equal(copy.Id,results[0].Loan!.CopyId);
        Assert.Equal(copy.InventoryCode,WebAppBookLibrary.Contracts.Loans.LoanResponse.From(results[0].Loan!,DateTime.UtcNow).CopyInventoryCode);
        Assert.False((await store.CollectPickupReservationAsync(reservation.Id,f.Actor.UserId,1,default,selection with {CopyId=MongoDB.Bson.ObjectId.GenerateNewId().ToString()})).Success);
        var unit=await db.GetCollection<BookCopy>("BookCopies").Find(c=>c.Id==copy.Id).SingleAsync();
        Assert.Equal("loaned",unit.Status);Assert.Equal(results[0].Loan!.Id,unit.ActiveAssignmentId);
        Assert.False(await store.ReturnPhysicalLoanAsync(results[0].Loan!.Id,f.Actor.UserId,default));
        var request=new WebAppBookLibrary.Contracts.Copies.ReturnPhysicalRequest(new(copy.Id,unit.Version),new("damaged","Repair","Wet pages"),"return-one");
        Assert.True(await store.ReturnPhysicalLoanAsync(results[0].Loan!.Id,f.Actor.UserId,default,request:request));
        Assert.True(await store.ReturnPhysicalLoanAsync(results[0].Loan!.Id,f.Actor.UserId,default,request:request));
        Assert.False(await store.ReturnPhysicalLoanAsync(results[0].Loan!.Id,f.Actor.UserId,default,request:request with {Inspection=new("good",null,null)}));
        unit=await db.GetCollection<BookCopy>("BookCopies").Find(c=>c.Id==copy.Id).SingleAsync();
        Assert.Equal("maintenance",unit.Status);Assert.Null(unit.ActiveAssignmentId);
        Assert.Equal(0,(await f.Db.Books.Find(b=>b.Id==f.Book.Id).SingleAsync()).AvailableCopies);
    });
    [LocalMongoFact]
    public Task Reservation_and_cancellation_move_the_same_unit_without_changing_total()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await CopyPersistenceTests.Seed(db);
        var copy=(await new CopyStore(f.Db,TimeProvider.System).CreateAsync(f.Book.Id,new("CIRC-001","good",null,null,"one"),f.Actor,default)).Copy!;
        var reader=await SupportPersistenceTests.UserAsync(f.Db,"user");
        var store=new MongoCirculationStore(f.Db,Options.Create(new CirculationOptions{Mode="active"}));
        var reservation=(await store.ReserveForPickupAsync(reader.Id,f.Book.Id,"reserve",default)).Reservation!;
        Assert.Equal(copy.Id,reservation.CopyId);
        var held=await db.GetCollection<BookCopy>("BookCopies").Find(c=>c.Id==copy.Id).SingleAsync();
        Assert.Equal("held",held.Status);Assert.Equal(reservation.Id,held.ActiveAssignmentId);
        Assert.True(await store.CancelPickupReservationAsync(reservation.Id,reader.Id,false,null,1,default));
        var released=await db.GetCollection<BookCopy>("BookCopies").Find(c=>c.Id==copy.Id).SingleAsync();
        Assert.Equal("available",released.Status);Assert.Null(released.ActiveAssignmentId);
        var book=await f.Db.Books.Find(b=>b.Id==f.Book.Id).SingleAsync();Assert.Equal(1,book.TotalCopies);Assert.Equal(1,book.AvailableCopies);
    });
}
