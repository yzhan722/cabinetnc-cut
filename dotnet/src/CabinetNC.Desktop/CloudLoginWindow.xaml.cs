using System.Windows;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Desktop.Core.Cloud;

namespace CabinetNC.Desktop;

/// <summary>
/// Collects server / tenant / email / password, signs in through a freshly built <see cref="CloudApiClient"/>
/// and hands the signed-in client back. The password never leaves this window.
/// </summary>
public partial class CloudLoginWindow : Window
{
    readonly Func<CloudSettings, CloudApiClient> _clientFactory;

    public CloudLoginWindow(CloudSettings current, Func<CloudSettings, CloudApiClient> clientFactory)
    {
        InitializeComponent();
        _clientFactory = clientFactory;
        ServerBox.Text = current.ServerUrl ?? "";
        TenantBox.Text = current.Tenant ?? "";
        EmailBox.Text = current.Email ?? "";
        Loaded += (_, _) => (string.IsNullOrEmpty(ServerBox.Text) ? ServerBox : (System.Windows.Controls.Control)PasswordBox).Focus();
    }

    /// <summary>Set when the dialog closes with a successful login.</summary>
    public CloudApiClient? Client { get; private set; }
    public CloudSettings? Settings { get; private set; }

    void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    async void OnLogin(object sender, RoutedEventArgs e)
    {
        if (!CloudClientOptions.TryParseServerUrl(ServerBox.Text, out var baseAddress, out var urlError))
        {
            ShowError(urlError);
            return;
        }
        var tenant = TenantBox.Text.Trim();
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;
        if (tenant.Length == 0 || email.Length == 0 || password.Length == 0)
        {
            ShowError("租户、邮箱和密码都需要填写。");
            return;
        }

        var settings = new CloudSettings(ComputeMode.Intranet, baseAddress.ToString(), tenant, email);
        CloudApiClient? client = null;
        try
        {
            SetBusy(true);
            client = _clientFactory(settings);
            await client.Session.LoginAsync(email, password, CancellationToken.None);
            Client = client;
            Settings = settings;
            DialogResult = true;
            Close();
        }
        catch (CloudApiException ex)
        {
            client?.Dispose();
            ShowError(ex.Code switch
            {
                ApiErrorCodes.InvalidCredentials => "邮箱或密码不正确。",
                ApiErrorCodes.RateLimited => "尝试次数过多，请稍后再试。",
                ApiErrorCodes.InvalidRequest => "服务器拒绝了登录请求：" + ex.Message,
                _ => "登录失败：" + ex.Message,
            });
        }
        catch (ComputeUnavailableException ex)
        {
            client?.Dispose();
            ShowError("连接不上服务器：" + (ex.InnerException?.Message ?? ex.Message)
                + "\n请检查地址、网络，以及本机是否已信任车间内部 CA 的根证书。");
        }
        finally
        {
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        LoginButton.Content = busy ? "登录中…" : "登录";
        ServerBox.IsEnabled = TenantBox.IsEnabled = EmailBox.IsEnabled = PasswordBox.IsEnabled = !busy;
    }

    void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
