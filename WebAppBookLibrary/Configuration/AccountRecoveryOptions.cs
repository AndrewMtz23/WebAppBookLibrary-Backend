namespace WebAppBookLibrary.Configuration;

public sealed class AccountRecoveryOptions
{
    public bool Enabled { get; set; }
    public string PublicBaseUrl { get; set; } = "http://localhost:4200";
    public string LocalMailDirectory { get; set; } = "";
    public int ResetMinutes { get; set; } = 30;
    public int VerificationMinutes { get; set; } = 1440;
}
