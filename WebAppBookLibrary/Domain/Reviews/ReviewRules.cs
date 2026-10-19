namespace WebAppBookLibrary.Domain.Reviews;
public static class ReviewRules
{
 public static bool ValidRating(int rating) => rating is >=1 and <=5;
 public static string VisibilityAfterSave(bool hold) => hold ? "hidden" : "visible";
 public static string VisibilityAfterRestore(string current) => current == "removed" ? "removed" : "visible";
 public static bool ValidPage(int page,int size) => page>0 && size is >=1 and <=100 && (long)(page-1)*size<=int.MaxValue;
 public static bool ValidReason(string? reason) => !string.IsNullOrWhiteSpace(reason) && reason.Length<=1000;
}
