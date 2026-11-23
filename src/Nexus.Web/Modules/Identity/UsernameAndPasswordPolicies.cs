using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace Nexus.Web.Modules.Identity;

public static partial class UsernamePolicy
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    private static readonly HashSet<string> ReservedUsernames = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "system", "support", "root", "nexus",
        "official", "security", "hr", "comms", "help", "info",
        "noreply", "postmaster", "moderator", "owner", "staff", "api"
    };

    [GeneratedRegex("^[a-z0-9._-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPattern();

    public static bool TryValidate(string? raw, out string normalized, out string? error)
    {
        normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length is < MinLength or > MaxLength ||
            !AllowedPattern().IsMatch(normalized) ||
            normalized.StartsWith('.') ||
            normalized.EndsWith('.') ||
            normalized.Contains("..", StringComparison.Ordinal))
        {
            error = "Username must be 3 to 32 lowercase letters, numbers, dots, underscores, or hyphens.";
            return false;
        }

        if (ReservedUsernames.Contains(normalized))
        {
            error = "This username is reserved. Choose another.";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// Enforces M1.4 / SEC-21 password policy: at least 12 characters and blocks common passwords.
/// </summary>
public sealed class CommonPasswordValidator : IPasswordValidator<NexusUser>
{
    public const int MinPasswordLength = 12;

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234", "password12345", "password123456", "123456789012", "1234567890123",
        "qwerty123456", "qwertyuiop12", "letmein12345", "welcome12345", "admin1234567",
        "iloveyou1234", "abc123456789", "changeme1234", "nexus1234567", "company12345",
        "aaaaaaaaaaaa", "000000000000", "111111111111", "passw0rd1234", "p@ssw0rd1234"
    };

    public Task<IdentityResult> ValidateAsync(UserManager<NexusUser> manager, NexusUser user, string? password)
    {
        var value = password ?? string.Empty;
        var errors = new List<IdentityError>();

        if (value.Length < MinPasswordLength)
        {
            errors.Add(new IdentityError
            {
                Code = "PasswordTooShort",
                Description = $"Password must be at least {MinPasswordLength} characters."
            });
        }

        var trimmedLower = value.Trim().ToLowerInvariant();
        if (CommonPasswords.Contains(trimmedLower) ||
            trimmedLower.StartsWith("password", StringComparison.Ordinal) ||
            trimmedLower.StartsWith("123456", StringComparison.Ordinal) ||
            trimmedLower.StartsWith("qwerty", StringComparison.Ordinal) ||
            (value.Length >= MinPasswordLength && value.Distinct().Count() <= 2))
        {
            errors.Add(new IdentityError
            {
                Code = "PasswordTooCommon",
                Description = "Choose a less common password."
            });
        }

        if (!string.IsNullOrWhiteSpace(user.UserName) &&
            user.UserName.Length >= 3 &&
            trimmedLower.Contains(user.UserName.ToLowerInvariant(), StringComparison.Ordinal))
        {
            errors.Add(new IdentityError
            {
                Code = "PasswordContainsUsername",
                Description = "Password must not contain your username."
            });
        }

        return Task.FromResult(errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]));
    }
}
