using System.ComponentModel.DataAnnotations;
namespace WebAppBookLibrary.Configuration;
public sealed class SupportOptions
{
    public bool AcceptNewTickets { get; set; } = true;
    [Range(1,100)] public int MaxOpenTickets { get; set; } = 5;
    [Range(1,1000)] public int CreatesPerHour { get; set; } = 3;
    [Range(1,1000)] public int MessagesPerHour { get; set; } = 20;
    [Range(1,30)] public int ReopenDays { get; set; } = 7;
    [Range(1,10)] public int RetentionYears { get; set; } = 1;
}
