using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Compute.Contracts;
using CabinetNC.Desktop.Core.Cloud;
using CabinetNC.Domain.Nesting;
using CabinetNC.Infrastructure.Diagnostics;
using CabinetNC.Infrastructure.Library;
using PanelPart = CabinetNC.Domain.Parts.Panel;
using StatusKind = CabinetNC.Desktop.Core.StatusSeverity;

namespace CabinetNC.Desktop;

/// <summary>
/// Intranet compute mode: settings, sign-in, and the one alternative branch of <c>RunNestAsync</c>.
/// Everything here is glue around <see cref="CabinetNC.Desktop.Core.Cloud"/>; the Local path is untouched.
/// </summary>
public partial class MainWindow
{
    CloudSettingsStore? _cloudSettingsStore;
    CloudSettings _cloudSettings = CloudSettings.Default;
    CloudApiClient? _cloud;
    bool _syncingComputeMode;
    Guid? _lastCloudJobId;
    string? _lastEngineVersion;

    /// <summary>Same folder as library.json, so the UI smoke's private library also isolates cloud state.</summary>
    static string CloudDataDirectory() =>
        Path.GetDirectoryName(WorkshopLibraryStore.DefaultPath()) ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CabinetNC");

#if CUSTOMER_BUILD
    /// <summary>Customer build: the compute engine is not shipped, so Intranet is the only mode.</summary>
    ComputeMode ComputeModeSelected => ComputeMode.Intranet;
#else
    ComputeMode ComputeModeSelected =>
        (ComputeModeCombo.SelectedItem as ComboBoxItem)?.Tag is "intranet" ? ComputeMode.Intranet : ComputeMode.Local;
#endif

    void InitializeCloud()
    {
        _cloudSettingsStore = new CloudSettingsStore(CloudDataDirectory());
        _cloudSettings = _cloudSettingsStore.Load();
        _syncingComputeMode = true;
#if CUSTOMER_BUILD
        _cloudSettings = _cloudSettings with { Mode = ComputeMode.Intranet };
        ComputeModeCombo.SelectedIndex = 1;
        ComputeModeCombo.IsEnabled = false;
        ComputeModeCombo.ToolTip = "客户版只支持内网计算：本机不包含排版引擎。";
#else
        ComputeModeCombo.SelectedIndex = _cloudSettings.Mode == ComputeMode.Intranet ? 1 : 0;
#endif
        _syncingComputeMode = false;
        if (!string.IsNullOrEmpty(_cloudSettings.ServerUrl) && !string.IsNullOrEmpty(_cloudSettings.Tenant))
        {
            try
            {
                _cloud = CreateCloudClient(_cloudSettings);
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException)
            {
                UsageLog.LogEvent("warn", "cloud.settings", error: ex.Message);
            }
        }
        UpdateCloudUi();
    }

    CloudApiClient CreateCloudClient(CloudSettings settings)
    {
        if (!CloudClientOptions.TryParseServerUrl(settings.ServerUrl, out var baseAddress, out var error))
            throw new ArgumentException(error);
        var dir = CloudDataDirectory();
        var options = new CloudClientOptions { BaseAddress = baseAddress, Tenant = settings.Tenant ?? "" };
        var client = new CloudApiClient(options, new WindowsTokenStore(dir), new DeviceIdentityStore(dir));
        client.Session.Changed += () => Dispatcher.BeginInvoke(UpdateCloudUi);
        return client;
    }

    async Task RestoreCloudSessionAsync()
    {
        if (_cloud is null) return;
        try
        {
            var restored = await _cloud.Session.TryRestoreAsync(CancellationToken.None);
            UsageLog.LogEvent("ui", "cloud.restore", new Dictionary<string, object?> { ["restored"] = restored, ["mode"] = _cloudSettings.Mode.ToString() });
        }
        catch (Exception ex) when (ex is CloudApiException or ComputeUnavailableException)
        {
            UsageLog.LogEvent("warn", "cloud.restore", error: ex.GetType().Name);
        }
        UpdateCloudUi();
    }

    void OnComputeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingComputeMode || _cloudSettingsStore is null) return;
        _cloudSettings = _cloudSettings with { Mode = ComputeModeSelected };
        _cloudSettingsStore.Save(_cloudSettings);
        UsageLog.LogEvent("ui", "cloud.mode", new Dictionary<string, object?> { ["mode"] = _cloudSettings.Mode.ToString() });
        if (_cloudSettings.Mode == ComputeMode.Intranet && _cloud?.Session.IsAuthenticated != true)
            SetStatus("内网计算需要先登录 · 点「内网登录…」", StatusKind.Warning);
        else
            SetStatus(_cloudSettings.Mode == ComputeMode.Intranet ? "排版将提交到内网服务器计算" : "排版在本机计算");
        UpdateCloudUi();
    }

    async void OnCloudLoginClick(object sender, RoutedEventArgs e)
    {
        if (_cloud?.Session.IsAuthenticated == true)
        {
            await _cloud.Session.LogoutAsync(CancellationToken.None);
            UsageLog.LogEvent("ui", "cloud.logout");
            SetStatus("已退出内网登录 · 刷新令牌已撤销", StatusKind.Warning);
            UpdateCloudUi();
            return;
        }

        var dlg = new CloudLoginWindow(_cloudSettings, CreateCloudClient) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Client is null || dlg.Settings is null)
            return;

        _cloud?.Dispose();
        _cloud = dlg.Client;
        _cloudSettings = dlg.Settings;
        _cloudSettingsStore?.Save(_cloudSettings);
        _syncingComputeMode = true;
        ComputeModeCombo.SelectedIndex = 1;
        _syncingComputeMode = false;
        UsageLog.LogEvent("ui", "cloud.login", new Dictionary<string, object?>
        {
            ["server"] = _cloudSettings.ServerUrl,
            ["tenant"] = _cloudSettings.Tenant,
            ["role"] = _cloud.Session.Role,
        });
        SetStatus($"已登录内网 · {_cloudSettings.Email} @ {_cloudSettings.Tenant} · 排版将提交到服务器计算", StatusKind.Success);
        UpdateCloudUi();
    }

    void UpdateCloudUi()
    {
        var signedIn = _cloud?.Session.IsAuthenticated == true;
        CloudLoginButton.Content = signedIn ? "退出内网登录" : "内网登录…";
        CloudStatusText.Text = signedIn
            ? $"已登录 {_cloud!.Session.Email} @ {_cloudSettings.Tenant} · {_cloudSettings.ServerUrl}"
            : "未登录";
        CloudStatusText.Foreground = signedIn ? (Brush)FindResource("SuccessBrush") : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

        if (ComputeModeSelected != ComputeMode.Intranet)
            return;
        // In Intranet mode the badge describes the server session, not the local worker.
        WorkerBadge.Text = signedIn ? $"计算引擎 · 内网 · {_cloud!.Session.Email}" : "计算引擎 · 内网 · 未登录";
        WorkerBadge.Foreground = (Brush)FindResource(signedIn ? "SuccessBrush" : "WarningBrush");
        WorkerBadge.ToolTip = signedIn
            ? $"排版提交到 {_cloudSettings.ServerUrl} 计算；租户 {_cloudSettings.Tenant}；access token 至 {_cloud!.Session.AccessTokenExpiresAtUtc:HH:mm:ss} UTC 自动刷新"
            : "已选择内网计算但尚未登录；不会自动改用本机计算。";
    }

    /// <summary>
    /// The Intranet branch of RunNestAsync. The v2 contract carries the same panels, stock queue, settings,
    /// engine preference and timeout the local branch would use, and the server runs the same router, so the
    /// result maps back 1:1 (true-shape placements, remnants, keep-outs, parts-in-part, group reports).
    /// Throws instead of falling back to Local.
    /// </summary>
    async Task<((NestResult Result, NestEngineRunLog Log) Packed, List<NestWarningMsg> Warnings)> RunIntranetNestAsync(
        IReadOnlyList<PanelPart> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> sheets,
        string enginePreference,
        TimeSpan advancedTimeout,
        CancellationToken ct)
    {
        var gateway = new ComputeGatewayFactory(
            () => throw new InvalidOperationException("Local gateway is not used from this path."),
            () => _cloud).Create(ComputeMode.Intranet);

        var (request, error) = NestRequestBuilder.BuildV2(panels, settings, sheets, enginePreference, advancedTimeout);
        if (request is null)
            throw new InvalidOperationException("排版输入不符合内网契约：" + error);
        var progress = new Progress<ComputeProgress>(p =>
            SetStatus($"内网计算中… {StatusLabel(p.Status)} · {p.Elapsed.TotalSeconds:0}s · job {ShortId(p.JobId)}", StatusKind.Busy));

        var result = await gateway.RunNestingV2Async(request, progress, ct).ConfigureAwait(true);
        _lastCloudJobId = result.JobId;
        _lastEngineVersion = result.EngineVersion;

        var packed = NestResultMapper.ToLocal(result);
        var warnings = new List<NestWarningMsg>
        {
            new()
            {
                Code = "intranet",
                Message = $"内网 job {result.JobId} · {result.EngineVersion} · 服务器计算 {result.DurationMs} ms · input {result.InputSha256[..12]} · result {result.ResultSha256[..12]}",
            },
        };
        return (packed, warnings);
    }

    static string StatusLabel(JobStatus status) => status switch
    {
        JobStatus.Queued => "排队中",
        JobStatus.Running => "计算中",
        JobStatus.Succeeded => "已完成",
        JobStatus.Failed => "失败",
        _ => status.ToString(),
    };

    static string ShortId(Guid id) => id.ToString("N")[..8];

    void DisposeCloud()
    {
        _cloud?.Dispose();
        _cloud = null;
    }
}
