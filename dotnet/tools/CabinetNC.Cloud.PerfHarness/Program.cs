using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Desktop.Core.Cloud;

namespace CabinetNC.Cloud.PerfHarness;

/// <summary>
/// Usage (password via environment variable, never on the command line):
///   set CABINETNC_PERF_PASSWORD=...
///   dotnet run -c Release --project dotnet/tools/CabinetNC.Cloud.PerfHarness -- --url http://127.0.0.1:8080 --tenant shop
///       --email admin@example.internal [--sizes 50,100,300,500] [--runs 5] [--concurrency 2,5] [--concurrency-size 100]
///       [--worker-container cabinetnc-intranet-cabinetnc-worker-1] [--out .handoff/local-evidence/perf]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var a = ParseArgs(args);
        var url = Required(a, "url");
        var tenant = Required(a, "tenant");
        var email = Required(a, "email");
        var password = Environment.GetEnvironmentVariable("CABINETNC_PERF_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Set CABINETNC_PERF_PASSWORD.");
            return 2;
        }
        var sizes = (a.GetValueOrDefault("sizes") ?? "50,100,300,500").Split(',').Select(int.Parse).ToArray();
        var runs = int.Parse(a.GetValueOrDefault("runs") ?? "5", CultureInfo.InvariantCulture);
        var concurrency = (a.GetValueOrDefault("concurrency") ?? "2,5").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        var concurrencySize = int.Parse(a.GetValueOrDefault("concurrency-size") ?? "100", CultureInfo.InvariantCulture);
        var workerContainer = a.GetValueOrDefault("worker-container");
        var outDir = a.GetValueOrDefault("out") ?? ".";
        Directory.CreateDirectory(outDir);

        if (!CloudClientOptions.TryParseServerUrl(url, out var baseAddress, out var urlError))
        {
            Console.Error.WriteLine(urlError);
            return 2;
        }
        var options = new CloudClientOptions
        {
            BaseAddress = baseAddress,
            Tenant = tenant,
            // Tight polling so the client-side end-to-end number is not dominated by poll slack.
            PollInitial = TimeSpan.FromMilliseconds(200),
            PollMax = TimeSpan.FromMilliseconds(500),
            JobTimeout = TimeSpan.FromMinutes(30),
        };
        var stateDir = Path.Combine(Path.GetTempPath(), "cabinetnc-perf-" + Guid.NewGuid().ToString("N"));
        using var client = new CloudApiClient(options, new NullTokenStore(), new DeviceIdentityStore(stateDir));
        await client.Session.LoginAsync(email, password, CancellationToken.None);
        Console.WriteLine($"logged in as {email} @ {tenant} ({client.Session.Role}) → {baseAddress}");

        var sampler = workerContainer is null ? null : new CgroupSampler(workerContainer);
        var results = new List<BatchResult>();

        foreach (var size in sizes)
        {
            var batch = await RunBatchAsync(client, options, $"seq-{size}", size, runs, parallel: 1, sampler);
            results.Add(batch);
            Print(batch);
        }
        foreach (var n in concurrency)
        {
            var batch = await RunBatchAsync(client, options, $"conc-{n}x{concurrencySize}", concurrencySize, n, parallel: n, sampler);
            results.Add(batch);
            Print(batch);
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(outDir, $"perf-{stamp}.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(new { generatedAtUtc = DateTime.UtcNow, server = baseAddress.ToString(), results }, new JsonSerializerOptions { WriteIndented = true }));
        var mdPath = Path.Combine(outDir, $"perf-{stamp}.md");
        await File.WriteAllTextAsync(mdPath, Markdown(results));
        Console.WriteLine();
        Console.WriteLine(Markdown(results));
        Console.WriteLine($"written: {jsonPath}");
        Console.WriteLine($"written: {mdPath}");
        try { Directory.Delete(stateDir, true); } catch { /* best effort */ }
        return results.All(r => r.Runs.All(x => x.Status == "Succeeded")) ? 0 : 1;
    }

    static async Task<BatchResult> RunBatchAsync(CloudApiClient client, CloudClientOptions options, string label, int panels, int jobs, int parallel, CgroupSampler? sampler)
    {
        var request = SyntheticCases.Build(panels);
        var before = sampler is null ? null : await sampler.SampleAsync();
        var wall = Stopwatch.StartNew();
        var runResults = new List<RunResult>();

        if (parallel <= 1)
        {
            for (var i = 0; i < jobs; i++)
                runResults.Add(await RunOneAsync(client, options, request));
        }
        else
        {
            var tasks = Enumerable.Range(0, jobs).Select(_ => RunOneAsync(client, options, request)).ToArray();
            runResults.AddRange(await Task.WhenAll(tasks));
        }

        wall.Stop();
        var after = sampler is null ? null : await sampler.SampleAsync();
        return new BatchResult(
            label, panels, jobs, parallel, runResults, wall.ElapsedMilliseconds,
            before is null || after is null ? null : (after.CpuUsageUsec - before.CpuUsageUsec) / 1000.0,
            after?.MemoryPeakBytes, after?.MemoryCurrentBytes);
    }

    static async Task<RunResult> RunOneAsync(CloudApiClient client, CloudClientOptions options, SubmitNestJobRequest request)
    {
        var gateway = new IntranetComputeGateway(client, options);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await gateway.RunNestingAsync(request, null, CancellationToken.None);
            sw.Stop();
            var status = await client.GetJobStatusAsync(result.JobId, CancellationToken.None);
            var queueWait = status.StartedAtUtc is { } s ? (s - status.CreatedAtUtc).TotalMilliseconds : double.NaN;
            var serverE2E = status.CompletedAtUtc is { } c ? (c - status.CreatedAtUtc).TotalMilliseconds : double.NaN;
            return new RunResult(result.JobId, "Succeeded", queueWait, result.DurationMs, serverE2E, sw.ElapsedMilliseconds, result.SheetCount, result.Unplaced.Count, status.AttemptCount, null);
        }
        catch (Exception ex) when (ex is ComputeJobFailedException or ComputeJobTimeoutException or ComputeUnavailableException or CloudApiException)
        {
            sw.Stop();
            var jobId = (ex as ComputeJobFailedException)?.JobId ?? (ex as ComputeJobTimeoutException)?.JobId ?? Guid.Empty;
            return new RunResult(jobId, ex is ComputeJobTimeoutException ? "Timeout" : "Failed", double.NaN, null, double.NaN, sw.ElapsedMilliseconds, 0, 0, 0, ex.Message);
        }
    }

    static void Print(BatchResult b)
    {
        Console.WriteLine($"[{b.Label}] panels={b.Panels} jobs={b.Jobs} parallel={b.Parallel} wall={b.WallMs} ms");
        foreach (var r in b.Runs)
            Console.WriteLine($"   {r.JobId:N} {r.Status,-9} queue={Fmt(r.QueueWaitMs)} compute={r.ComputeMs} serverE2E={Fmt(r.ServerEndToEndMs)} clientE2E={r.ClientEndToEndMs} sheets={r.SheetCount} unplaced={r.Unplaced} attempts={r.Attempts}{(r.Error is null ? "" : " " + r.Error)}");
        if (b.WorkerCpuMs is not null)
            Console.WriteLine($"   worker cpu={b.WorkerCpuMs:0} ms total ({b.WorkerCpuMs / Math.Max(1, b.Jobs):0} ms/job) peakMem={b.WorkerMemoryPeakBytes / 1048576.0:0.0} MiB currentMem={b.WorkerMemoryCurrentBytes / 1048576.0:0.0} MiB");
    }

    static string Fmt(double v) => double.IsNaN(v) ? "n/a" : v.ToString("0", CultureInfo.InvariantCulture);

    static string Markdown(List<BatchResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| case | panels | jobs | parallel | status | queue wait ms (min/med/max) | compute ms (min/med/max) | server e2e ms (min/med/max) | client e2e ms (min/med/max) | sheets | worker CPU ms/job | worker peak mem MiB | batch wall ms |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var b in results)
        {
            var ok = b.Runs.Where(r => r.Status == "Succeeded").ToList();
            string Stat(Func<RunResult, double> f) => ok.Count == 0 ? "n/a" : $"{ok.Min(f):0} / {Median(ok.Select(f)):0} / {ok.Max(f):0}";
            var status = ok.Count == b.Runs.Count ? "all Succeeded" : $"{ok.Count}/{b.Runs.Count} Succeeded";
            var sheets = ok.Count == 0 ? "n/a" : string.Join("/", ok.Select(r => r.SheetCount).Distinct().Order());
            var cpu = b.WorkerCpuMs is null ? "n/a" : (b.WorkerCpuMs.Value / Math.Max(1, b.Jobs)).ToString("0", CultureInfo.InvariantCulture);
            var mem = b.WorkerMemoryPeakBytes is null ? "n/a" : (b.WorkerMemoryPeakBytes.Value / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
            sb.AppendLine($"| {b.Label} | {b.Panels} | {b.Jobs} | {b.Parallel} | {status} | {Stat(r => r.QueueWaitMs)} | {Stat(r => r.ComputeMs ?? 0)} | {Stat(r => r.ServerEndToEndMs)} | {Stat(r => r.ClientEndToEndMs)} | {sheets} | {cpu} | {mem} | {b.WallMs} |");
        }
        return sb.ToString();
    }

    static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return double.NaN;
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            map[key] = value;
        }
        return map;
    }

    static string Required(Dictionary<string, string> a, string key) =>
        a.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required");

    sealed class NullTokenStore : ITokenStore
    {
        public StoredRefreshToken? Load() => null;
        public void Save(StoredRefreshToken token) { }
        public void Clear() { }
    }
}

public sealed record RunResult(
    Guid JobId, string Status, double QueueWaitMs, long? ComputeMs, double ServerEndToEndMs, long ClientEndToEndMs,
    int SheetCount, int Unplaced, int Attempts, string? Error);

public sealed record BatchResult(
    string Label, int Panels, int Jobs, int Parallel, List<RunResult> Runs, long WallMs,
    double? WorkerCpuMs, long? WorkerMemoryPeakBytes, long? WorkerMemoryCurrentBytes);

/// <summary>Deterministic, clearly synthetic cases — not shop jobs. Same seed ⇒ same request bytes ⇒ same hashes.</summary>
public static class SyntheticCases
{
    public static SubmitNestJobRequest Build(int panels)
    {
        var rng = new Random(20260906 + panels);
        var parts = new List<NestPartDto>(panels);
        for (var i = 0; i < panels; i++)
        {
            var w = Math.Round(200 + rng.NextDouble() * 1000, 1);   // 200..1200 mm
            var h = Math.Round(150 + rng.NextDouble() * 650, 1);    // 150..800 mm
            var material = i % 4 == 3 ? "SYN-PLY" : "SYN-MDF";
            parts.Add(new NestPartDto($"SYN-{panels}-{i:0000}", w, h, i % 3 != 0, material, 18));
        }
        return new SubmitNestJobRequest(parts, 1220, 2440, 12, 15, true);
    }
}

/// <summary>Reads the worker container's cgroup v2 counters through docker exec (cumulative CPU, peak memory).</summary>
public sealed class CgroupSampler(string container)
{
    public sealed record Sample(long CpuUsageUsec, long MemoryPeakBytes, long MemoryCurrentBytes);

    public async Task<Sample?> SampleAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", $"exec {container} sh -c \"cat /sys/fs/cgroup/cpu.stat; echo PEAK $(cat /sys/fs/cgroup/memory.peak 2>/dev/null || echo 0); echo CURRENT $(cat /sys/fs/cgroup/memory.current)\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) return null;
            long cpu = 0, peak = 0, current = 0;
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;
                switch (parts[0])
                {
                    case "usage_usec": cpu = long.Parse(parts[1], CultureInfo.InvariantCulture); break;
                    case "PEAK": peak = long.Parse(parts[1], CultureInfo.InvariantCulture); break;
                    case "CURRENT": current = long.Parse(parts[1], CultureInfo.InvariantCulture); break;
                }
            }
            return new Sample(cpu, peak, current);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
