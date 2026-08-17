using System.Text.RegularExpressions;

namespace WebAppBookLibrary.Services
{
    public static class PasswordValidator
    {

        private static readonly Regex PasswordRegex = new Regex(
            pattern: @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{5,}$",
            options: RegexOptions.Compiled,
            matchTimeout: TimeSpan.FromMilliseconds(250)); 

        public static bool IsValid(string? password)
        {
            if (string.IsNullOrWhiteSpace(password))
                return false;

            return PasswordRegex.IsMatch(password);
        }
    }
}
