using WebAppBookLibrary.Domain.Circulation;

namespace WebAppBookLibrary.Configuration;

public sealed class CirculationOptions
{
    public const string SectionName = "Circulation";

    public string Mode { get; set; } = CirculationModes.Legacy;
    public int PickupHours { get; set; } = 48;
    public int LoanDays { get; set; } = 14;
    public int RenewalDays { get; set; } = 7;
    public int MaxRenewals { get; set; } = 1;
    public int PollSeconds { get; set; } = 30;
}
