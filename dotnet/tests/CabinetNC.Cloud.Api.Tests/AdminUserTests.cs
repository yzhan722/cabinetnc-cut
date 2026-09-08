using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CabinetNC.Cloud.Api.Auth;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure.Entities;
using CabinetNC.Cloud.Infrastructure.Tests;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CabinetNC.Cloud.Api.Tests;

/// <summary>
/// Commercial gate G1: a shop administrator onboards operators, disables leavers, recovers lost passwords and
/// kicks lost laptops without touching the database; operators change their own password.
/// </summary>
public class AdminUserTests(MigratedByAppPostgresFixture pg) : IClassFixture<MigratedByAppPostgresFixture>, IAsyncLifetime
{
    const string OperatorEmail = "operator@example.internal";
    const string OperatorPassword = "operator-initial-pass";

    readonly ManualClock _clock = new() { Now = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, DateTimeOffset.UtcNow.Day, DateTimeOffset.UtcNow.Hour, DateTimeOffset.UtcNow.Minute, DateTimeOffset.UtcNow.Second, TimeSpan.Zero) };
    CloudApiFactory _factory = null!;
    HttpClient _client = null!;
    LoginResponse _admin = null!;

    public async Task InitializeAsync()
    {
        if (!PostgresAvailability.IsAvailable)
            return;
        await pg.ResetAsync();
        _factory = new CloudApiFactory(pg.ConnectionString, _clock);
        _client = _factory.CreateClient();
        _admin = await LoginAsync(CloudApiFactory.AdminEmail, CloudApiFactory.AdminPassword, "ADMIN-PC");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    async Task<LoginResponse> LoginAsync(string email, string password, string deviceName, string? deviceKey = null)
    {
        var response = await _client.PostJsonAsync(ApiRoutes.AuthLogin,
            new LoginRequest(CloudApiFactory.Tenant, email, password, deviceKey ?? Guid.NewGuid().ToString("D"), deviceName));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<LoginResponse>();
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, object? body, string? bearer)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null)
            request.Content = new StringContent(CloudJson.Serialize(body), Encoding.UTF8, "application/json");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _client.SendAsync(request);
    }

    async Task<UserSummary> CreateOperatorAsync(string email = OperatorEmail, string password = OperatorPassword)
    {
        var response = await SendAsync(HttpMethod.Post, ApiRoutes.AdminUsers, new CreateUserRequest(email, password, UserRoles.Operator), _admin.AccessToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<UserSummary>();
    }

    async Task<List<string>> AuditTypesAsync(Guid? userId = null)
    {
        await using var db = pg.CreateContext();
        var q = db.AuditEvents.AsQueryable();
        if (userId is not null)
            q = q.Where(a => a.UserId == userId);
        return await q.OrderBy(a => a.Id).Select(a => a.EventType).ToListAsync();
    }

    [PostgresFact]
    public async Task Admin_creates_an_operator_who_can_log_in_and_is_listed()
    {
        var created = await CreateOperatorAsync("  Operator@Example.INTERNAL ");

        Assert.Equal(OperatorEmail, created.Email);
        Assert.Equal(UserRoles.Operator, created.Role);
        Assert.True(created.IsActive);
        Assert.Equal(0, created.DeviceCount);

        var login = await LoginAsync(OperatorEmail, OperatorPassword, "OP-PC");
        Assert.Equal(UserRoles.Operator, login.Role);

        var list = await (await SendAsync(HttpMethod.Get, ApiRoutes.AdminUsers, null, _admin.AccessToken)).ReadAsync<List<UserSummary>>();
        Assert.Equal([CloudApiFactory.AdminEmail, OperatorEmail], list.Select(u => u.Email).Order());
        var op = list.Single(u => u.Email == OperatorEmail);
        Assert.Equal(1, op.DeviceCount);
        Assert.Equal(1, op.ActiveSessions);
        Assert.Equal(_clock.Now, op.LastSeenAtUtc);
        Assert.Contains("user.created", await AuditTypesAsync(created.Id));
    }

    [PostgresFact]
    public async Task Creation_validates_email_role_and_password_policy()
    {
        var badEmail = await SendAsync(HttpMethod.Post, ApiRoutes.AdminUsers, new CreateUserRequest("not-an-email", OperatorPassword, UserRoles.Operator), _admin.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, badEmail.StatusCode);

        var badRole = await SendAsync(HttpMethod.Post, ApiRoutes.AdminUsers, new CreateUserRequest("x@example.internal", OperatorPassword, "superuser"), _admin.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);

        var weak = await SendAsync(HttpMethod.Post, ApiRoutes.AdminUsers, new CreateUserRequest("x@example.internal", "short", UserRoles.Operator), _admin.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("12", (await weak.ReadErrorAsync()).Message);

        await CreateOperatorAsync();
        var duplicate = await SendAsync(HttpMethod.Post, ApiRoutes.AdminUsers, new CreateUserRequest(OperatorEmail.ToUpperInvariant(), OperatorPassword, UserRoles.Operator), _admin.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(ApiErrorCodes.Conflict, (await duplicate.ReadErrorAsync()).Code);

        await using var db = pg.CreateContext();
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.DoesNotContain(await db.Users.Select(u => u.PasswordHash).ToListAsync(), h => h == OperatorPassword);
    }

    [PostgresFact]
    public async Task Operators_cannot_administer_and_other_tenants_are_invisible()
    {
        var created = await CreateOperatorAsync();
        var op = await LoginAsync(OperatorEmail, OperatorPassword, "OP-PC");

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, ApiRoutes.AdminUsers, null, op.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, ApiRoutes.AdminUsers, new CreateUserRequest("z@example.internal", OperatorPassword, UserRoles.Admin), op.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, ApiRoutes.AdminUsers, null, null)).StatusCode);

        var otherAdmin = await OtherTenantAdminTokenAsync();
        var list = await (await SendAsync(HttpMethod.Get, ApiRoutes.AdminUsers, null, otherAdmin)).ReadAsync<List<UserSummary>>();
        Assert.DoesNotContain(list, u => u.Email == OperatorEmail || u.Email == CloudApiFactory.AdminEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, ApiRoutes.ForAdminUser(created.Id), new UpdateUserRequest(null, false), otherAdmin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, ApiRoutes.ForAdminUserPassword(created.Id), new ResetPasswordRequest("another-long-password"), otherAdmin)).StatusCode);
    }

    [PostgresFact]
    public async Task Deactivating_a_user_ends_their_sessions_and_blocks_login_until_reactivated()
    {
        var created = await CreateOperatorAsync();
        var op = await LoginAsync(OperatorEmail, OperatorPassword, "OP-PC");

        var patched = await (await SendAsync(HttpMethod.Patch, ApiRoutes.ForAdminUser(created.Id), new UpdateUserRequest(null, false), _admin.AccessToken)).ReadAsync<UserSummary>();
        Assert.False(patched.IsActive);
        Assert.Equal(0, patched.ActiveSessions);

        var refresh = await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(op.RefreshToken, op.DeviceId));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        var login = await _client.PostJsonAsync(ApiRoutes.AuthLogin, new LoginRequest(CloudApiFactory.Tenant, OperatorEmail, OperatorPassword, op.DeviceId, "OP-PC"));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidCredentials, (await login.ReadErrorAsync()).Code);

        var reactivated = await (await SendAsync(HttpMethod.Patch, ApiRoutes.ForAdminUser(created.Id), new UpdateUserRequest(UserRoles.Admin, true), _admin.AccessToken)).ReadAsync<UserSummary>();
        Assert.True(reactivated.IsActive);
        Assert.Equal(UserRoles.Admin, reactivated.Role);
        var again = await LoginAsync(OperatorEmail, OperatorPassword, "OP-PC");
        Assert.Equal(UserRoles.Admin, again.Role);
        Assert.Contains("user.updated", await AuditTypesAsync(created.Id));
    }

    [PostgresFact]
    public async Task The_last_active_admin_cannot_be_deactivated_or_demoted()
    {
        var adminId = Guid.Parse(_admin.UserId);

        var deactivate = await SendAsync(HttpMethod.Patch, ApiRoutes.ForAdminUser(adminId), new UpdateUserRequest(null, false), _admin.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, deactivate.StatusCode);
        Assert.Equal(ApiErrorCodes.Conflict, (await deactivate.ReadErrorAsync()).Code);

        var demote = await SendAsync(HttpMethod.Patch, ApiRoutes.ForAdminUser(adminId), new UpdateUserRequest(UserRoles.Operator, null), _admin.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        // With a second admin in place the first one may step down.
        await SendAsync(HttpMethod.Post, ApiRoutes.AdminUsers, new CreateUserRequest("second-admin@example.internal", "second-admin-password", UserRoles.Admin), _admin.AccessToken);
        var demoteNow = await SendAsync(HttpMethod.Patch, ApiRoutes.ForAdminUser(adminId), new UpdateUserRequest(UserRoles.Operator, null), _admin.AccessToken);
        Assert.Equal(HttpStatusCode.OK, demoteNow.StatusCode);
    }

    [PostgresFact]
    public async Task Admin_password_reset_signs_the_user_out_everywhere()
    {
        var created = await CreateOperatorAsync();
        var laptop = await LoginAsync(OperatorEmail, OperatorPassword, "LAPTOP");
        var shopPc = await LoginAsync(OperatorEmail, OperatorPassword, "SHOP-PC");

        var weak = await SendAsync(HttpMethod.Post, ApiRoutes.ForAdminUserPassword(created.Id), new ResetPasswordRequest("tooshort"), _admin.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var reset = await SendAsync(HttpMethod.Post, ApiRoutes.ForAdminUserPassword(created.Id), new ResetPasswordRequest("operator-new-password-1"), _admin.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(laptop.RefreshToken, laptop.DeviceId))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(shopPc.RefreshToken, shopPc.DeviceId))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostJsonAsync(ApiRoutes.AuthLogin, new LoginRequest(CloudApiFactory.Tenant, OperatorEmail, OperatorPassword, laptop.DeviceId, "LAPTOP"))).StatusCode);
        await LoginAsync(OperatorEmail, "operator-new-password-1", "LAPTOP", laptop.DeviceId);
        Assert.Contains("user.password_reset", await AuditTypesAsync(created.Id));
    }

    [PostgresFact]
    public async Task Users_change_their_own_password_and_only_other_devices_are_signed_out()
    {
        await CreateOperatorAsync();
        var laptop = await LoginAsync(OperatorEmail, OperatorPassword, "LAPTOP");
        var shopPc = await LoginAsync(OperatorEmail, OperatorPassword, "SHOP-PC");

        var wrong = await SendAsync(HttpMethod.Post, ApiRoutes.AuthPassword, new ChangePasswordRequest("not-my-password", "operator-new-password-2"), laptop.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidCredentials, (await wrong.ReadErrorAsync()).Code);

        var weak = await SendAsync(HttpMethod.Post, ApiRoutes.AuthPassword, new ChangePasswordRequest(OperatorPassword, "short"), laptop.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var changed = await SendAsync(HttpMethod.Post, ApiRoutes.AuthPassword, new ChangePasswordRequest(OperatorPassword, "operator-new-password-2"), laptop.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // The device that changed the password keeps its session; the other one is out.
        Assert.Equal(HttpStatusCode.OK, (await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(laptop.RefreshToken, laptop.DeviceId))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(shopPc.RefreshToken, shopPc.DeviceId))).StatusCode);
        await LoginAsync(OperatorEmail, "operator-new-password-2", "SHOP-PC", shopPc.DeviceId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, ApiRoutes.AuthPassword, new ChangePasswordRequest("x", "y"), null)).StatusCode);
    }

    [PostgresFact]
    public async Task Devices_are_listed_and_a_lost_device_can_be_revoked()
    {
        var created = await CreateOperatorAsync();
        var laptop = await LoginAsync(OperatorEmail, OperatorPassword, "LAPTOP");
        var shopPc = await LoginAsync(OperatorEmail, OperatorPassword, "SHOP-PC");

        var devices = await (await SendAsync(HttpMethod.Get, ApiRoutes.AdminDevices, null, _admin.AccessToken)).ReadAsync<List<DeviceSummary>>();
        Assert.Equal(3, devices.Count);
        var laptopDevice = devices.Single(d => d.DeviceKey == laptop.DeviceId);
        Assert.Equal(OperatorEmail, laptopDevice.UserEmail);
        Assert.Equal("LAPTOP", laptopDevice.DeviceName);
        Assert.Equal(1, laptopDevice.ActiveSessions);

        var filtered = await (await SendAsync(HttpMethod.Get, $"{ApiRoutes.AdminDevices}?userId={created.Id:D}", null, _admin.AccessToken)).ReadAsync<List<DeviceSummary>>();
        Assert.Equal(2, filtered.Count);

        var revoke = await SendAsync(HttpMethod.Post, ApiRoutes.ForAdminDeviceRevoke(laptopDevice.Id), null, _admin.AccessToken);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(1, (await revoke.ReadAsync<RevokeResponse>()).RevokedSessions);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(laptop.RefreshToken, laptop.DeviceId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(shopPc.RefreshToken, shopPc.DeviceId))).StatusCode);
        // A revoked device can sign in again with the password; revocation is about sessions, not a ban.
        await LoginAsync(OperatorEmail, OperatorPassword, "LAPTOP", laptop.DeviceId);

        var revokeAll = await SendAsync(HttpMethod.Post, ApiRoutes.ForAdminUserRevoke(created.Id), null, _admin.AccessToken);
        Assert.Equal(HttpStatusCode.OK, revokeAll.StatusCode);
        Assert.Equal(2, (await revokeAll.ReadAsync<RevokeResponse>()).RevokedSessions);
        Assert.Contains("device.revoked", await AuditTypesAsync());
        Assert.Contains("user.sessions_revoked", await AuditTypesAsync(created.Id));

        var otherAdmin = await OtherTenantAdminTokenAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, ApiRoutes.ForAdminDeviceRevoke(laptopDevice.Id), null, otherAdmin)).StatusCode);
    }

    async Task<string> OtherTenantAdminTokenAsync()
    {
        await using var db = pg.CreateContext();
        var tenant = new TenantEntity { Id = Guid.CreateVersion7(), Name = "tenant-b", CreatedAtUtc = _clock.Now };
        var user = new UserEntity { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Email = "admin@tenant-b.test", PasswordHash = "", Role = UserRoles.Admin, CreatedAtUtc = _clock.Now };
        user.PasswordHash = new PasswordHasher<UserEntity>().HashPassword(user, "tenant-b-admin-password");
        var device = new DeviceEntity { Id = Guid.CreateVersion7(), TenantId = tenant.Id, UserId = user.Id, DeviceKey = Guid.NewGuid().ToString("D"), CreatedAtUtc = _clock.Now };
        db.AddRange(tenant, user, device);
        await db.SaveChangesAsync();
        return _factory.Services.GetRequiredService<AccessTokenIssuer>().Issue(new AccessTokenSubject(user.Id, tenant.Id, device.Id, user.Role));
    }
}
