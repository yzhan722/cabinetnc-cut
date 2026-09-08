using System.Text;
using System.Threading.RateLimiting;
using CabinetNC.Cloud.Api;
using CabinetNC.Cloud.Api.Admin;
using CabinetNC.Cloud.Api.Auth;
using CabinetNC.Cloud.Api.Http;
using CabinetNC.Cloud.Api.Jobs;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure;
using CabinetNC.Cloud.Infrastructure.Entities;
using CabinetNC.Cloud.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
// SQL text per request is debugging output, not operations logging (spec §11 wants correlation/job events).
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
// The API is stateless (JWT + opaque refresh tokens in PostgreSQL); the Data Protection key ring is
// initialised eagerly by the framework but never used, so its ephemeral-key warnings are noise.
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

builder.Services.AddSingleton(_ => CloudApiOptions.FromEnvironment());
builder.Services.AddCloudPersistence(provider =>
    provider.GetRequiredService<CloudApiOptions>().DbConnection);
builder.Services.AddCloudObjectStore(_ => ObjectStoreOptions.FromEnvironment());
builder.Services.AddSingleton<IPasswordHasher<UserEntity>, PasswordHasher<UserEntity>>();
builder.Services.AddSingleton<PasswordVerifier>();
builder.Services.AddSingleton<AccessTokenIssuer>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<NestJobService>();
builder.Services.AddScoped<TenantAdminService>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

var trustedProxies = TrustedProxies.FromEnvironment();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    foreach (var network in trustedProxies)
        options.KnownIPNetworks.Add(network);
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<CloudApiOptions, TimeProvider>((options, apiOptions, clock) =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = apiOptions.JwtIssuer,
            ValidateAudience = true,
            ValidAudience = apiOptions.JwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(apiOptions.JwtSigningKey)),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = apiOptions.ClockSkew,
            NameClaimType = "sub",
            RoleClaimType = "role",
            // Lifetime follows the application clock (the issuer's clock), so expiry is testable and
            // consistent with the refresh-token checks instead of silently using DateTime.UtcNow.
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                if (expires is null)
                    throw new SecurityTokenNoExpirationException("The token has no expiration.");
                if (notBefore is { } nbf && nbf > now.Add(parameters.ClockSkew))
                    throw new SecurityTokenNotYetValidException("The token is not yet valid.");
                if (expires.Value.Add(parameters.ClockSkew) < now)
                    throw new SecurityTokenExpiredException("The token has expired.");
                return true;
            },
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                var expired = IsTokenExpired(context.AuthenticateFailure);
                context.Response.Headers.WWWAuthenticate = expired
                    ? "Bearer error=\"invalid_token\", error_description=\"The access token has expired.\""
                    : "Bearer";
                await ApiResponse.WriteErrorAsync(
                    context.HttpContext,
                    StatusCodes.Status401Unauthorized,
                    expired ? ApiErrorCodes.TokenExpired : ApiErrorCodes.Unauthorized,
                    expired ? "The access token has expired." : "Authentication is required.",
                    context.HttpContext.RequestAborted);
            },
            OnForbidden = context =>
                ApiResponse.WriteErrorAsync(
                    context.HttpContext,
                    StatusCodes.Status403Forbidden,
                    ApiErrorCodes.Unauthorized,
                    "You are not allowed to perform this action.",
                    context.HttpContext.RequestAborted),
        };
    });
builder.Services
    .AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        await ApiResponse.WriteErrorAsync(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            ApiErrorCodes.RateLimited,
            "Too many requests; try again later.",
            ct);
    };
    options.AddPolicy("login", context => FixedWindow(context, permitLimit: 10));
    options.AddPolicy("refresh", context => FixedWindow(context, permitLimit: 30));
    options.AddPolicy("logout", context => FixedWindow(context, permitLimit: 60));
});

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ApiExceptionMiddleware>();
// Must precede the rate limiter so its per-client partition sees the real client behind the proxy.
if (trustedProxies.Count > 0)
    app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet(ApiRoutes.Health, (TimeProvider clock) =>
        Results.Json(
            new HealthResponse(
                Status: "ok",
                Service: "cabinetnc-cloud-api",
                Version: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                TimestampUtc: clock.GetUtcNow()),
            CloudJson.Options))
    .AllowAnonymous();

// Readiness for operators and load balancers: real round trips to PostgreSQL and the object store, each
// bounded to two seconds, plus the queue depth. Anonymous, but reveals nothing beyond ok/failed + counts.
app.MapGet(ApiRoutes.HealthReady, async (CloudDbContext db, IObjectStore objects, TimeProvider clock, CancellationToken ct) =>
    {
        var database = await ProbeAsync(async token =>
        {
            var queued = await db.ComputeJobs.CountAsync(j => j.Status == JobStatus.Queued, token);
            var running = await db.ComputeJobs.CountAsync(j => j.Status == JobStatus.Running, token);
            return (queued, running);
        }, ct);
        var objectStore = await ProbeAsync(async token =>
        {
            await objects.ExistsAsync("health/readiness-probe", token);
            return (0, 0);
        }, ct);

        var ready = database.Health.Status == "ok" && objectStore.Health.Status == "ok";
        var response = new ReadinessResponse(
            ready ? "ready" : "not_ready",
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
            clock.GetUtcNow(),
            database.Health,
            objectStore.Health,
            database.Counts.queued,
            database.Counts.running);
        return Results.Json(response, CloudJson.Options, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    })
    .AllowAnonymous();

app.MapPost(ApiRoutes.AuthLogin, async (
        LoginRequest request,
        AuthService auth,
        HttpContext context,
        CancellationToken ct) =>
    {
        PreventTokenResponseCaching(context);
        return Results.Json(
            await auth.LoginAsync(request, context.GetCorrelationId(), ct),
            CloudJson.Options);
    })
    .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
    .RequireRateLimiting("login")
    .AllowAnonymous();

app.MapPost(ApiRoutes.AuthRefresh, async (
        RefreshRequest request,
        AuthService auth,
        HttpContext context,
        CancellationToken ct) =>
    {
        PreventTokenResponseCaching(context);
        return Results.Json(
            await auth.RefreshAsync(request, context.GetCorrelationId(), ct),
            CloudJson.Options);
    })
    .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
    .RequireRateLimiting("refresh")
    .AllowAnonymous();

app.MapPost(ApiRoutes.AuthLogout, async (
        LogoutRequest request,
        AuthService auth,
        HttpContext context,
        CancellationToken ct) =>
    {
        await auth.LogoutAsync(request, context.User, context.GetCorrelationId(), ct);
        return Results.NoContent();
    })
    .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
    .RequireRateLimiting("logout")
    .RequireAuthorization();

app.MapNestJobEndpoints();
app.MapAdminEndpoints();
app.MapAdminUserEndpoints();

app.MapFallback((HttpContext context) =>
        Results.Json(
            new ApiError(
                ApiErrorCodes.InvalidRequest,
                "The API endpoint was not found.",
                context.GetCorrelationId()),
            CloudJson.Options,
            statusCode: StatusCodes.Status404NotFound))
    .AllowAnonymous();

await BootstrapAdmin.MigrateAndSeedAsync(app.Services);
app.Run();

static RateLimitPartition<string> FixedWindow(HttpContext context, int permitLimit)
{
    var partition = context.Connection.RemoteIpAddress?.ToString() ?? "unknown-client";
    return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    });
}

static async Task<(DependencyHealth Health, (int queued, int running) Counts)> ProbeAsync(
    Func<CancellationToken, Task<(int queued, int running)>> probe,
    CancellationToken ct)
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(2));
    try
    {
        var counts = await probe(timeout.Token);
        return (new DependencyHealth("ok", stopwatch.ElapsedMilliseconds, null), counts);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return (new DependencyHealth("failed", stopwatch.ElapsedMilliseconds, "timeout"), (0, 0));
    }
    catch (Exception ex)
    {
        // Type name only: connection strings and hostnames stay out of an anonymous endpoint.
        return (new DependencyHealth("failed", stopwatch.ElapsedMilliseconds, ex.GetType().Name), (0, 0));
    }
}

static bool IsTokenExpired(Exception? exception) =>
    exception is SecurityTokenExpiredException
    || (exception?.InnerException is not null && IsTokenExpired(exception.InnerException));

static void PreventTokenResponseCaching(HttpContext context)
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
}

public partial class Program;
