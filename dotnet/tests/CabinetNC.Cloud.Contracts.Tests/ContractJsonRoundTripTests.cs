using System.Text.Json;
using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Cloud.Contracts.Tests;

/// <summary>
/// Desktop and the intranet API must agree byte-for-byte on the wire format: camelCase names,
/// declaration order, enum values as the spec's status strings, nulls written explicitly.
/// Task 7 hashes the canonical request JSON, so the exact string is pinned here.
/// </summary>
public class ContractJsonRoundTripTests
{
    static T RoundTrip<T>(T value) => CloudJson.Deserialize<T>(CloudJson.Serialize(value));

    static SubmitNestJobRequest SampleNestRequest() =>
        new(
            Parts:
            [
                new NestPartDto("A", 600, 400, true, "MDF", 18),
                new NestPartDto("B", 350.5, 250, false, null, 0),
            ],
            SheetWidthMm: 1220,
            SheetLengthMm: 2440,
            SpacingMm: 12,
            BorderMm: 15,
            AllowRotation: true);

    [Fact]
    public void SubmitNestJobRequest_canonical_json_is_stable()
    {
        const string expected =
            "{\"parts\":[" +
            "{\"panelId\":\"A\",\"widthMm\":600,\"heightMm\":400,\"mayRotate\":true,\"material\":\"MDF\",\"thicknessMm\":18}," +
            "{\"panelId\":\"B\",\"widthMm\":350.5,\"heightMm\":250,\"mayRotate\":false,\"material\":null,\"thicknessMm\":0}" +
            "],\"sheetWidthMm\":1220,\"sheetLengthMm\":2440,\"spacingMm\":12,\"borderMm\":15,\"allowRotation\":true}";

        var json = CloudJson.Serialize(SampleNestRequest());

        Assert.Equal(expected, json);
        Assert.Equal(expected, CloudJson.Serialize(CloudJson.Deserialize<SubmitNestJobRequest>(json)));
    }

    [Fact]
    public void SubmitNestJobRequest_round_trips_values()
    {
        var back = RoundTrip(SampleNestRequest());

        Assert.Equal(2, back.Parts.Count);
        Assert.Equal("A", back.Parts[0].PanelId);
        Assert.Equal(350.5, back.Parts[1].WidthMm);
        Assert.False(back.Parts[1].MayRotate);
        Assert.Null(back.Parts[1].Material);
        Assert.Equal(1220, back.SheetWidthMm);
        Assert.Equal(2440, back.SheetLengthMm);
        Assert.Equal(12, back.SpacingMm);
        Assert.Equal(15, back.BorderMm);
        Assert.True(back.AllowRotation);
    }

    [Fact]
    public void SubmitNestJobResponse_round_trips()
    {
        var jobId = Guid.Parse("6f1c2b9e-0d5a-4c6e-9a1b-2f3e4d5c6b7a");
        var json = CloudJson.Serialize(new SubmitNestJobResponse(jobId, JobStatus.Queued, "corr-1"));

        Assert.Equal("{\"jobId\":\"6f1c2b9e-0d5a-4c6e-9a1b-2f3e4d5c6b7a\",\"status\":\"Queued\",\"correlationId\":\"corr-1\"}", json);
        var back = CloudJson.Deserialize<SubmitNestJobResponse>(json);
        Assert.Equal(jobId, back.JobId);
        Assert.Equal(JobStatus.Queued, back.Status);
        Assert.Equal("corr-1", back.CorrelationId);
    }

    [Theory]
    [InlineData(JobStatus.Queued, "Queued")]
    [InlineData(JobStatus.Running, "Running")]
    [InlineData(JobStatus.Succeeded, "Succeeded")]
    [InlineData(JobStatus.Failed, "Failed")]
    public void JobStatus_serializes_as_spec_strings(JobStatus status, string expected)
    {
        Assert.Equal($"\"{expected}\"", CloudJson.Serialize(status));
        Assert.Equal(status, CloudJson.Deserialize<JobStatus>($"\"{expected}\""));
    }

    [Fact]
    public void JobStatus_rejects_unknown_and_numeric_values()
    {
        Assert.Throws<JsonException>(() => CloudJson.Deserialize<JobStatus>("\"Cancelled\""));
        Assert.Throws<JsonException>(() => CloudJson.Deserialize<JobStatus>("2"));
    }

    [Fact]
    public void JobStatusResponse_round_trips_with_null_optionals()
    {
        var created = new DateTimeOffset(2026, 9, 6, 6, 30, 0, TimeSpan.Zero);
        var queued = new JobStatusResponse(
            JobId: Guid.NewGuid(),
            JobType: JobTypes.Nest,
            Status: JobStatus.Queued,
            AttemptCount: 0,
            CreatedAtUtc: created,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            ErrorCode: null,
            ErrorMessage: null,
            DurationMs: null,
            CorrelationId: "corr-2");

        var json = CloudJson.Serialize(queued);
        Assert.Contains("\"startedAtUtc\":null", json);
        Assert.Contains("\"durationMs\":null", json);
        Assert.Contains("\"createdAtUtc\":\"2026-09-06T06:30:00+00:00\"", json);

        var back = RoundTrip(queued);
        Assert.Equal(queued.JobId, back.JobId);
        Assert.Equal("nest", back.JobType);
        Assert.Equal(JobStatus.Queued, back.Status);
        Assert.Equal(created, back.CreatedAtUtc);
        Assert.Null(back.StartedAtUtc);
        Assert.Null(back.ErrorCode);
        Assert.Null(back.DurationMs);

        var failed = queued with
        {
            Status = JobStatus.Failed,
            AttemptCount = 3,
            StartedAtUtc = created.AddSeconds(1),
            CompletedAtUtc = created.AddSeconds(4),
            ErrorCode = ApiErrorCodes.ComputeFailed,
            ErrorMessage = "boom",
            DurationMs = 3000,
        };
        var backFailed = RoundTrip(failed);
        Assert.Equal(JobStatus.Failed, backFailed.Status);
        Assert.Equal(3, backFailed.AttemptCount);
        Assert.Equal("compute_failed", backFailed.ErrorCode);
        Assert.Equal(3000, backFailed.DurationMs);
        Assert.Equal(created.AddSeconds(4), backFailed.CompletedAtUtc);
    }

    [Fact]
    public void NestJobResult_round_trips_with_nullable_warning_fields()
    {
        var result = new NestJobResult(
            JobId: Guid.NewGuid(),
            Engine: "grouped_blf_v0",
            EngineVersion: "1.0.0+abc1234",
            Placements: [new NestPlacementDto("A", 0, 15, 15, 0), new NestPlacementDto("B", 0, 627, 15, 90)],
            SheetCount: 1,
            Unplaced: ["BIG"],
            Warnings:
            [
                new NestWarningDto("engine_fallback", "TimeoutException: advanced_stub: timeout", null, null, null),
                new NestWarningDto("aabb_gap", "spacing/collision A × B on sheet 0", "A", "B", 0),
            ],
            InputSha256: new string('a', 64),
            ResultSha256: new string('b', 64),
            DurationMs: 42);

        var json = CloudJson.Serialize(result);
        Assert.Contains("\"panelIdA\":null,\"panelIdB\":null,\"sheetIndex\":null", json);
        Assert.Contains("\"rotationDeg\":90", json);
        Assert.Contains("\"inputSha256\":\"" + new string('a', 64) + "\"", json);

        var back = RoundTrip(result);
        Assert.Equal(result.JobId, back.JobId);
        Assert.Equal("grouped_blf_v0", back.Engine);
        Assert.Equal("1.0.0+abc1234", back.EngineVersion);
        Assert.Equal(result.Placements, back.Placements);
        Assert.Equal(1, back.SheetCount);
        Assert.Equal(["BIG"], back.Unplaced);
        Assert.Equal(result.Warnings, back.Warnings);
        Assert.Equal(result.ResultSha256, back.ResultSha256);
        Assert.Equal(42, back.DurationMs);
        Assert.Equal(json, CloudJson.Serialize(back));
    }

    [Fact]
    public void NestJobResultPayload_canonical_json_is_stable()
    {
        // The worker hashes exactly these bytes into ResultSha256, so the layout is pinned.
        var payload = new NestJobResultPayload(
            Engine: "grouped_blf_v0",
            Placements: [new NestPlacementDto("A", 0, 15, 15, 0), new NestPlacementDto("B", 0, 627, 15, 90)],
            SheetCount: 1,
            Unplaced: [],
            Warnings: [new NestWarningDto("aabb_gap", "spacing/collision A x B on sheet 0", "A", "B", 0)]);

        var json = CloudJson.Serialize(payload);

        Assert.Equal(
            "{\"engine\":\"grouped_blf_v0\",\"placements\":[" +
            "{\"panelId\":\"A\",\"sheetIndex\":0,\"offsetX\":15,\"offsetY\":15,\"rotationDeg\":0}," +
            "{\"panelId\":\"B\",\"sheetIndex\":0,\"offsetX\":627,\"offsetY\":15,\"rotationDeg\":90}" +
            "],\"sheetCount\":1,\"unplaced\":[],\"warnings\":[" +
            "{\"code\":\"aabb_gap\",\"message\":\"spacing/collision A x B on sheet 0\",\"panelIdA\":\"A\",\"panelIdB\":\"B\",\"sheetIndex\":0}" +
            "]}",
            json);
        Assert.Equal(json, CloudJson.Serialize(CloudJson.Deserialize<NestJobResultPayload>(json)));
    }

    [Fact]
    public void Auth_requests_and_responses_round_trip_with_camelCase_names()
    {
        var login = new LoginRequest("shop-a", "admin@example.test", "correct horse battery staple", "3b6d8d38-1d1a-4a8e-9c1f-0f8a2c0f1e11", "SHOP-PC-01");
        var loginJson = CloudJson.Serialize(login);
        Assert.Equal(
            "{\"tenant\":\"shop-a\",\"email\":\"admin@example.test\",\"password\":\"correct horse battery staple\"," +
            "\"deviceId\":\"3b6d8d38-1d1a-4a8e-9c1f-0f8a2c0f1e11\",\"deviceName\":\"SHOP-PC-01\"}",
            loginJson);
        Assert.Equal(login, RoundTrip(login));

        var loginResponse = new LoginResponse(
            AccessToken: "eyJ.access",
            AccessTokenExpiresInSeconds: 900,
            RefreshToken: "opaque-refresh",
            RefreshTokenExpiresInSeconds: 2_592_000,
            TenantId: "tenant-a",
            UserId: "user-1",
            DeviceId: login.DeviceId,
            Role: "admin");
        var loginResponseJson = CloudJson.Serialize(loginResponse);
        Assert.StartsWith("{\"accessToken\":\"eyJ.access\",\"accessTokenExpiresInSeconds\":900,\"refreshToken\":\"opaque-refresh\",\"refreshTokenExpiresInSeconds\":2592000,", loginResponseJson);
        Assert.Equal(loginResponse, RoundTrip(loginResponse));

        var refresh = new RefreshRequest("opaque-refresh", login.DeviceId);
        Assert.Equal("{\"refreshToken\":\"opaque-refresh\",\"deviceId\":\"3b6d8d38-1d1a-4a8e-9c1f-0f8a2c0f1e11\"}", CloudJson.Serialize(refresh));
        Assert.Equal(refresh, RoundTrip(refresh));

        var refreshResponse = new RefreshResponse("eyJ.access2", 900, "opaque-refresh-2", 2_592_000);
        Assert.Equal(
            "{\"accessToken\":\"eyJ.access2\",\"accessTokenExpiresInSeconds\":900,\"refreshToken\":\"opaque-refresh-2\",\"refreshTokenExpiresInSeconds\":2592000}",
            CloudJson.Serialize(refreshResponse));
        Assert.Equal(refreshResponse, RoundTrip(refreshResponse));

        var logout = new LogoutRequest("opaque-refresh-2", login.DeviceId);
        Assert.Equal(logout, RoundTrip(logout));
    }

    [Fact]
    public void ApiError_matches_spec_shape()
    {
        const string specSample = "{\"code\":\"job_not_found\",\"message\":\"Job was not found.\",\"correlationId\":\"c-123\"}";

        var error = CloudJson.Deserialize<ApiError>(specSample);
        Assert.Equal(ApiErrorCodes.JobNotFound, error.Code);
        Assert.Equal("Job was not found.", error.Message);
        Assert.Equal("c-123", error.CorrelationId);
        Assert.Equal(specSample, CloudJson.Serialize(error));
    }

    [Fact]
    public void ApiErrorCodes_cover_spec_list()
    {
        string[] spec =
        [
            "invalid_credentials", "invalid_device", "token_expired", "refresh_invalid", "refresh_reuse_detected",
            "unauthorized", "invalid_request", "idempotency_conflict", "job_not_found", "job_not_ready",
            "compute_failed", "storage_failed",
        ];
        // Added by the API on top of the spec minimum (HTTP 429, 500 and administrative 409 need stable codes too).
        string[] additions = ["rate_limited", "internal_error", "conflict"];

        Assert.Equal([.. spec, .. additions], ApiErrorCodes.All);
        Assert.Equal(ApiErrorCodes.All.Count, ApiErrorCodes.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void HealthResponse_round_trips()
    {
        var health = new HealthResponse("ok", "cabinetnc-cloud-api", "0.1.0+abc", new DateTimeOffset(2026, 9, 6, 7, 0, 0, TimeSpan.Zero));

        var json = CloudJson.Serialize(health);

        Assert.Equal("{\"status\":\"ok\",\"service\":\"cabinetnc-cloud-api\",\"version\":\"0.1.0\\u002Babc\",\"timestampUtc\":\"2026-09-06T07:00:00+00:00\"}", json);
        Assert.Equal(health, RoundTrip(health));
    }

    [Fact]
    public void ApiRoutes_and_headers_pin_spec_paths()
    {
        Assert.Equal("/api/v1/auth/login", ApiRoutes.AuthLogin);
        Assert.Equal("/api/v1/auth/refresh", ApiRoutes.AuthRefresh);
        Assert.Equal("/api/v1/auth/logout", ApiRoutes.AuthLogout);
        Assert.Equal("/api/v1/jobs/nest", ApiRoutes.JobsNest);
        Assert.Equal("/api/v1/jobs/{jobId}", ApiRoutes.JobStatusTemplate);
        Assert.Equal("/api/v1/jobs/{jobId}/result", ApiRoutes.JobResultTemplate);
        Assert.Equal("/api/v1/health", ApiRoutes.Health);

        var id = Guid.Parse("6f1c2b9e-0d5a-4c6e-9a1b-2f3e4d5c6b7a");
        Assert.Equal("/api/v1/jobs/6f1c2b9e-0d5a-4c6e-9a1b-2f3e4d5c6b7a", ApiRoutes.ForJobStatus(id));
        Assert.Equal("/api/v1/jobs/6f1c2b9e-0d5a-4c6e-9a1b-2f3e4d5c6b7a/result", ApiRoutes.ForJobResult(id));

        Assert.Equal("Idempotency-Key", ApiHeaders.IdempotencyKey);
        Assert.Equal("X-Correlation-ID", ApiHeaders.CorrelationId);
    }

    [Fact]
    public void Deserialize_is_case_insensitive_and_ignores_unknown_properties()
    {
        // A newer API may add fields; an older Desktop must still parse the response.
        const string json = "{\"JobId\":\"6f1c2b9e-0d5a-4c6e-9a1b-2f3e4d5c6b7a\",\"status\":\"Running\",\"correlationId\":\"c\",\"futureField\":{\"x\":1}}";

        var back = CloudJson.Deserialize<SubmitNestJobResponse>(json);

        Assert.Equal(Guid.Parse("6f1c2b9e-0d5a-4c6e-9a1b-2f3e4d5c6b7a"), back.JobId);
        Assert.Equal(JobStatus.Running, back.Status);
    }

    [Fact]
    public void Deserialize_null_or_empty_body_throws_instead_of_returning_null()
    {
        Assert.Throws<JsonException>(() => CloudJson.Deserialize<ApiError>("null"));
        Assert.Throws<JsonException>(() => CloudJson.Deserialize<ApiError>(""));
    }
}
