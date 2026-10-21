using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using WebAppBookLibrary.Domain.Copies;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Tests;

public sealed class CopyRulesTests
{
    [Fact]
    public void Codes_normalize_without_accepting_unicode_or_invalid_lengths()
    {
        Assert.Equal("AB-001", CopyRules.NormalizeCode(" ab-001 "));
        foreach (var code in new[] { "ab", new string('a',65), "ABC/1", "ébc", "ＡBC", "a b", "   " })
            Assert.Throws<ArgumentException>(() => CopyRules.NormalizeCode(code));
        Assert.Equal(new string('A',64), CopyRules.NormalizeCode(new string('a',64)));
    }

    [Theory]
    [InlineData("available","good",false,true)]
    [InlineData("available","worn",false,true)]
    [InlineData("available","damaged",false,false)]
    [InlineData("available",null,true,false)]
    [InlineData("available","good",true,false)]
    [InlineData("held","good",false,false)]
    [InlineData("loaned","good",false,false)]
    [InlineData("maintenance","good",false,false)]
    [InlineData("lost","good",false,false)]
    [InlineData("retired","good",false,false)]
    public void Only_inspected_available_units_can_be_lent(string state,string? condition,bool pending,bool expected)
        => Assert.Equal(expected,CopyRules.IsLendable(new BookCopy { Status=state,Condition=condition,InspectionPending=pending }));

    [Theory]
    [InlineData("available","maintenance",false,true)]
    [InlineData("available","lost",false,true)]
    [InlineData("maintenance","available",false,true)]
    [InlineData("lost","maintenance",false,true)]
    [InlineData("lost","available",true,false)]
    [InlineData("held","available",true,false)]
    [InlineData("loaned","available",true,false)]
    [InlineData("available","retired",false,false)]
    [InlineData("available","retired",true,true)]
    [InlineData("maintenance","retired",true,true)]
    [InlineData("lost","retired",true,true)]
    [InlineData("retired","available",true,false)]
    [InlineData("held","retired",true,false)]
    public void Generic_transitions_cannot_break_commitments_or_restore_retired_units(string from,string to,bool admin,bool expected)
        => Assert.Equal(expected,CopyRules.CanTransition(from,to,admin));

    [Fact]
    public void Old_documents_remain_aggregate_without_fictional_copy_links()
    {
        var book=BsonSerializer.Deserialize<Book>(new BsonDocument("Title","Legacy"));
        Assert.Equal("aggregate",book.InventoryMode);
        Assert.Null(BsonSerializer.Deserialize<Loan>(new BsonDocument()).CopyId);
        Assert.Null(BsonSerializer.Deserialize<PickupReservation>(new BsonDocument()).CopyId);
    }

    [Fact]
    public void Notes_have_a_hard_limit_and_unknown_conditions_are_not_accepted()
    {
        Assert.True(CopyRules.ValidNotes(new string('x',1000)));
        Assert.False(CopyRules.ValidNotes(new string('x',1001)));
        Assert.True(CopyRules.ValidNotes(null));
        Assert.True(CopyRules.ValidCondition("worn"));
        Assert.False(CopyRules.ValidCondition("excellent"));
        Assert.False(CopyRules.ValidCondition(null));
    }
}
