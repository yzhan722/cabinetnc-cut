namespace CabinetNC.Cloud.Contracts;

/// <summary>Roles carried in the JWT <c>role</c> claim and stored on users.</summary>
public static class UserRoles
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public static IReadOnlyList<string> All { get; } = [Admin, Operator];
}

/// <summary>Shared by server validation and the Desktop dialogs so both reject the same passwords.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 1024;

    /// <summary>Returns null when acceptable, otherwise a human-readable reason.</summary>
    public static string? Check(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password))
            return "A password is required.";
        if (password.Length < MinLength)
            return $"The password must be at least {MinLength} characters.";
        if (password.Length > MaxLength)
            return $"The password must be at most {MaxLength} characters.";
        if (password.Trim().Length != password.Length)
            return "The password must not start or end with whitespace.";
        if (email is not null && string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
            return "The password must not equal the email address.";
        return null;
    }
}

public sealed record CreateUserRequest(string Email, string Password, string Role);

/// <summary>PATCH semantics: only non-null members are applied.</summary>
public sealed record UpdateUserRequest(string? Role, bool? IsActive);

public sealed record ResetPasswordRequest(string NewPassword);

/// <summary>Self-service password change; other devices of the user are signed out.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UserSummary(
    Guid Id,
    string Email,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    int DeviceCount,
    int ActiveSessions,
    DateTimeOffset? LastSeenAtUtc);

public sealed record DeviceSummary(
    Guid Id,
    Guid UserId,
    string UserEmail,
    string DeviceKey,
    string? DeviceName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    int ActiveSessions);

public sealed record RevokeResponse(int RevokedSessions);
