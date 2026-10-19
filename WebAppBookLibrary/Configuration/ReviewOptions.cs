using System.ComponentModel.DataAnnotations;
namespace WebAppBookLibrary.Configuration;
public sealed class ReviewOptions
{
 public bool AcceptContributions {get;set;} = true;
 [Range(1,10000)] public int PublicationsPerHour {get;set;} = 20;
 [Range(1,10000)] public int ReportsPerHour {get;set;} = 10;
 [Range(1,10)] public int RetentionYears {get;set;} = 1;
}
