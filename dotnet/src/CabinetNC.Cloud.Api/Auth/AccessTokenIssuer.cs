using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CabinetNC.Cloud.Api.Auth;

public sealed record AccessTokenSubject(
    Guid UserId,
    Guid TenantId,
    Guid DeviceId,
    string Role);

public sealed class AccessTokenIssuer(CloudApiOptions options, TimeProvider clock)
{
    readonly SigningCredentials _signingCredentials = new(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSigningKey)),
        SecurityAlgorithms.HmacSha256);

    public string Issue(AccessTokenSubject subject)
    {
        var now = clock.GetUtcNow();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject.UserId.ToString("D")),
                new Claim("tenant_id", subject.TenantId.ToString("D")),
                new Claim("device_id", subject.DeviceId.ToString("D")),
                new Claim("role", subject.Role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString("D")),
            ]),
            Issuer = options.JwtIssuer,
            Audience = options.JwtAudience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(options.AccessTokenLifetime).UtcDateTime,
            SigningCredentials = _signingCredentials,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
