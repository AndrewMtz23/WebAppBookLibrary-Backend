using WebAppBookLibrary.Domain.Reviews;
namespace WebAppBookLibrary.Tests;
public sealed class ReviewRulesTests
{
    [Theory]
    [InlineData(0,false)] [InlineData(1,true)] [InlineData(5,true)] [InlineData(6,false)]
    public void Ratings_reject_out_of_range_values(int value,bool valid) => Assert.Equal(valid,ReviewRules.ValidRating(value));
    [Fact]
    public void Editing_and_restoring_cannot_bypass_moderation_or_author_removal()
    {
        Assert.Equal("hidden",ReviewRules.VisibilityAfterSave(true));
        Assert.Equal("visible",ReviewRules.VisibilityAfterSave(false));
        Assert.Equal("removed",ReviewRules.VisibilityAfterRestore("removed"));
        Assert.Equal("visible",ReviewRules.VisibilityAfterRestore("hidden"));
    }
    [Fact]
    public void Pages_and_private_reasons_have_safe_bounds()
    {
        Assert.False(ReviewRules.ValidPage(int.MaxValue,100));
        Assert.False(ReviewRules.ValidPage(0,20));
        Assert.False(ReviewRules.ValidPage(1,101));
        Assert.True(ReviewRules.ValidPage(1,100));
        Assert.False(ReviewRules.ValidReason("   "));
        Assert.False(ReviewRules.ValidReason(new string('x',1001)));
        Assert.True(ReviewRules.ValidReason("Spam"));
    }
}
