using System.Text.RegularExpressions;

namespace WebAppBookLibrary.Services
{
    public static class EmailValidator
    {
        private static readonly Regex EmailRegex = new Regex(
            pattern: @"^[^\s]+@[^\s]+\.[^\s]+$",
            options: RegexOptions.Compiled | RegexOptions.IgnoreCase,
            matchTimeout: TimeSpan.FromMilliseconds(250)); 

        public static bool IsValid(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            return EmailRegex.IsMatch(email);
        }
    }
}
