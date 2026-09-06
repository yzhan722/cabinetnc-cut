namespace CabinetNC.Desktop.Core.Cloud;

/// <summary>Timeouts and polling rules from the design spec (§13): short requests, async jobs, bounded waits.</summary>
public sealed class CloudClientOptions
{
    public required Uri BaseAddress { get; init; }

    /// <summary>Tenant slug sent with login; the JWT's tenant id always comes from the server.</summary>
    public required string Tenant { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan PollInitial { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan PollMax { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Total wall time a job may take before the client gives up (the job itself keeps running server-side).</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long transport failures are retried before the operation is reported as unavailable.</summary>
    public TimeSpan NetworkGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Refresh the access token when it has less than this left.</summary>
    public TimeSpan RefreshLeadTime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Accepts an operator-typed server address. Credentials and tokens only travel over HTTPS;
    /// plain HTTP is allowed solely for loopback (developer machine, UI smoke against a local API).
    /// </summary>
    public static bool TryParseServerUrl(string? text, out Uri baseAddress, out string error)
    {
        baseAddress = null!;
        error = "";
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "请输入服务器地址，例如 https://cabinetnc.shop.local";
            return false;
        }
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            error = "服务器地址格式不正确。";
            return false;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            error = "内网服务必须通过 https 访问（只有本机 127.0.0.1 / localhost 允许 http）。";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "服务器地址必须以 https:// 开头。";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "服务器地址不能带查询参数。";
            return false;
        }
        var path = uri.AbsolutePath.TrimEnd('/');
        baseAddress = new UriBuilder(uri.Scheme, uri.Host, uri.Port, path + "/").Uri;
        return true;
    }
}
