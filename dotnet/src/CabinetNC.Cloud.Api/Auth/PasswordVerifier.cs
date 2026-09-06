using System.Security.Cryptography;
using CabinetNC.Cloud.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;

namespace CabinetNC.Cloud.Api.Auth;

/// <summary>
/// Verifies a real or dummy PBKDF2 hash so an unknown email does not skip the expensive password
/// operation and become a simple timing oracle. Public responses remain identical either way.
/// </summary>
public sealed class PasswordVerifier
{
    readonly IPasswordHasher<UserEntity> _hasher;
    readonly UserEntity _dummyUser;
    readonly string _dummyHash;

    public PasswordVerifier(IPasswordHasher<UserEntity> hasher)
    {
        _hasher = hasher;
        _dummyUser = new UserEntity
        {
            Id = Guid.Empty,
            TenantId = Guid.Empty,
            Email = "dummy@invalid",
            PasswordHash = "",
            Role = "none",
            IsActive = false,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };
        _dummyHash = hasher.HashPassword(
            _dummyUser,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public PasswordVerificationResult Verify(UserEntity? user, string suppliedPassword)
    {
        var result = _hasher.VerifyHashedPassword(
            user ?? _dummyUser,
            user?.PasswordHash ?? _dummyHash,
            suppliedPassword);
        return user is null ? PasswordVerificationResult.Failed : result;
    }

    public string Hash(UserEntity user, string password) => _hasher.HashPassword(user, password);
}
