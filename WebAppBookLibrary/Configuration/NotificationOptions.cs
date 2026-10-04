namespace WebAppBookLibrary.Configuration;

public sealed class NotificationOptions
{
    public bool Enabled { get; set; } = true;
    public int EarlyHours { get; set; } = 72;
    public int SoonHours { get; set; } = 24;
    public int PollSeconds { get; set; } = 30;
}
