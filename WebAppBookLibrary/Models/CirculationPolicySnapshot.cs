namespace WebAppBookLibrary.Models;

public sealed class CirculationPolicySnapshot
{
    public string PolicyVersion { get; set; } = "circulation-v1";
    public int PickupHours { get; set; } = 48;
    public int LoanDays { get; set; } = 14;
    public int RenewalDays { get; set; } = 7;
    public int MaxRenewals { get; set; } = 1;
}
