using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CabinetNC.Desktop;

public partial class CloudJobsWindow : Window
{
    readonly Func<(string? Problem, CloudUploadBundle? Bundle)> _prepare;
    readonly Action<string> _uploaded;
    readonly Action<string> _openLocal;
    CloudJobClient? _client;
    CancellationTokenSource? _upload;
    bool _uploading;
    bool _loadedOnce;

    public CloudJobsWindow(
        Func<(string? Problem, CloudUploadBundle? Bundle)> prepare,
        Action<string> uploaded,
        Action<string> openLocal)
    {
        _prepare = prepare;
        _uploaded = uploaded;
        _openLocal = openLocal;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            // Hide/Show raises Loaded again. A second load disposed the client mid-upload.
            if (_loadedOnce) return;
            _loadedOnce = true;
            await ReloadAsync();
        };
        Closed += (_, _) =>
        {
            _upload?.Cancel();
            _client?.Dispose();
        };
    }

    async void OnRefreshClick(object sender, RoutedEventArgs e) => await ReloadAsync();

    void OnJobSelected(object sender, SelectionChangedEventArgs e)
    {
        var on = !_uploading && JobList.SelectedItem is Row;
        DeleteBtn.IsEnabled = on;
        OpenBtn.IsEnabled = on;
    }

    void OnJobDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (JobList.SelectedItem is Row) OnOpenClick(sender, e);
    }

    async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_client is null || _uploading || JobList.SelectedItem is not Row row) return;
        DeleteBtn.IsEnabled = false;
        OpenBtn.IsEnabled = false;
        UploadBtn.IsEnabled = false;
        RefreshBtn.IsEnabled = false;
        try
        {
            var path = CloudJobClient.LocalPathFor(row.Id);
            await _client.DownloadProjectAsync(row.Id, path, new Progress<string>(s => StatusText.Text = s), CancellationToken.None);
            _openLocal(path);
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            var on = JobList.SelectedItem is Row;
            DeleteBtn.IsEnabled = on;
            OpenBtn.IsEnabled = on;
            UploadBtn.IsEnabled = _client is not null;
            RefreshBtn.IsEnabled = _client is not null;
        }
    }

    async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_client is null || _uploading || JobList.SelectedItem is not Row row) return;
        var go = UiDialog.Show(this,
            "从云端删除「" + row.Name + "」？\n车间将不能再看到这份工程。",
            "删除云端工程",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (go != MessageBoxResult.Yes) return;
        DeleteBtn.IsEnabled = false;
        UploadBtn.IsEnabled = false;
        RefreshBtn.IsEnabled = false;
        try
        {
            StatusText.Text = "正在删除「" + row.Name + "」…";
            await _client.DeleteAsync(row.Id, CancellationToken.None);
            await ReloadAsync();
            StatusText.Text = "已删除「" + row.Name + "」。";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            DeleteBtn.IsEnabled = JobList.SelectedItem is Row;
            UploadBtn.IsEnabled = _client is not null;
            RefreshBtn.IsEnabled = _client is not null;
        }
    }

    async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            StatusText.Text = "这台电脑还没有接上车间云端。";
            return;
        }
        UploadBtn.IsEnabled = false;
        RefreshBtn.IsEnabled = false;
        _upload = new CancellationTokenSource();
        try
        {
            _uploading = true;
            StatusText.Text = "正在准备工程和新代开料包…";
            var (problem, bundle) = _prepare();
            if (problem is not null || bundle is null)
            {
                StatusText.Text = problem ?? "没有可上传的内容。";
                return;
            }
            var name = bundle.ProjectName;
            await _client.UploadAsync(bundle, new Progress<string>(s => StatusText.Text = s), _upload.Token);
            _uploaded(name);
            _uploading = false;
            await ReloadAsync();
            StatusText.Text = "已上传「" + name + "」。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "上传已取消。";
        }
        catch (Exception ex)
        {
            var msg = ex is AggregateException agg
                ? agg.Flatten().InnerExceptions.FirstOrDefault()?.Message ?? ex.Message
                : ex.Message;
            StatusText.Text = msg;
            Infrastructure.Diagnostics.UsageLog.LogActionResult("cloud.upload", new Dictionary<string, object?>
            {
                ["ok"] = false,
            }, error: msg);
        }
        finally
        {
            _uploading = false;
            UploadBtn.IsEnabled = _client is not null;
            RefreshBtn.IsEnabled = _client is not null;
            _upload?.Dispose();
            _upload = null;
        }
    }

    async Task ReloadAsync()
    {
        if (_uploading) return;
        _client?.Dispose();
        _client = CloudJobClient.TryLoad(out var problem);
        UploadBtn.IsEnabled = _client is not null;
        RefreshBtn.IsEnabled = _client is not null;
        if (_client is null)
        {
            JobList.ItemsSource = null;
            StatusText.Text = problem ?? "这台电脑还没有接上车间云端。";
            return;
        }
        try
        {
            StatusText.Text = "正在读取云端工程…";
            var rows = await _client.ListAsync(CancellationToken.None);
            JobList.ItemsSource = rows.Select(r => new Row(r)).ToList();
            StatusText.Text = rows.Count == 0 ? "云端还没有工程。" : $"云端 {rows.Count} 个工程。";
        }
        catch (Exception ex)
        {
            JobList.ItemsSource = null;
            StatusText.Text = "读云端列表失败：" + ex.Message;
        }
    }

    sealed class Row
    {
        public Row(CloudJobRow job)
        {
            Id = job.Id;
            Name = string.IsNullOrWhiteSpace(job.Name) ? job.Id : job.Name;
            Sheets = job.SheetCount.ToString(CultureInfo.InvariantCulture);
            When = DateTimeOffset.TryParse(job.UploadedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : job.UploadedAt;
        }

        public string Id { get; }
        public string Name { get; }
        public string When { get; }
        public string Sheets { get; }
    }
}
