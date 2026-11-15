using WebAppBookLibrary.Domain.Branches;

namespace WebAppBookLibrary.Tests;

public sealed class BranchRulesTests
{
    [Fact]
    public void Codes_are_canonical_and_timezone_is_iana()
    {
        Assert.Equal("CENTRO-1", BranchRules.NormalizeCode(" centro-1 "));
        Assert.Throws<ArgumentException>(() => BranchRules.NormalizeCode("../otra"));
        Assert.Throws<ArgumentException>(() => BranchRules.NormalizeCode(""));
        Assert.True(BranchRules.ValidTimeZone("America/Mexico_City"));
        Assert.False(BranchRules.ValidTimeZone("Central Standard Time"));
        Assert.False(BranchRules.ValidTimeZone("America/Inventada"));
    }
}
