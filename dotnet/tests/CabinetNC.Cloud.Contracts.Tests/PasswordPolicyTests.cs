using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Cloud.Contracts.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("correct-horse-battery")]
    [InlineData("十二个汉字也是可以接受的密码")]
    public void Accepts_long_enough_passwords(string password) =>
        Assert.Null(PasswordPolicy.Check(password, "op@example.internal"));

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("short11char", "at least 12")]
    [InlineData(" padded-with-space-", "whitespace")]
    [InlineData("op@example.internal", "email")]
    public void Rejects_weak_or_malformed_passwords(string? password, string reasonFragment)
    {
        var reason = PasswordPolicy.Check(password, "op@example.internal");
        Assert.NotNull(reason);
        Assert.Contains(reasonFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_absurdly_long_passwords() =>
        Assert.Contains("at most", PasswordPolicy.Check(new string('x', PasswordPolicy.MaxLength + 1)));
}
