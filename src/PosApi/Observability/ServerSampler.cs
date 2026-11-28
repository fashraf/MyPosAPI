using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Options;
using PosApi.Tenancy;
using SqlKata.Execution;

namespace PosApi.Observability;

/// <summary>Live server load (CPU, memory, disk, database connections, requests) and a sample every minute in control.server_sample.</summary>
public sealed class ServerSampler(IServiceProvider services, IOptions<TenancyOptions> options, ILogger<ServerSampler> logger) : BackgroundService
{
    private static int _requests, _errors;
    public static void CountRequest(int status) { Interlocked.Increment(ref _requests); if (status >= 500) Interlocked.Increment(ref _errors); }

    public sealed record Load(double CpuPct, double MemoryPct, long MemoryUsedBytes, long MemoryTotalBytes, int ProcessMb, double DiskFreePct,
        long DiskFreeBytes, long DiskTotalBytes, int DbConnections, int Threads, int ThreadPoolBusy, int RequestsLastMinute, int ErrorsLastMinute, DateTime AtUtc);

    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTime _lastAt = DateTime.UtcNow;
    private int _lastRequests, _lastErrors;
    public Load? Current { get; private set; }

    /// <summary>CPU used by this API since the last reading, as a share of all cores.</summary>
    private double Cpu()
    {
        var p = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var used = (p.TotalProcessorTime - _lastCpu).TotalMilliseconds;
        var wall = (now - _lastAt).TotalMilliseconds * Environment.ProcessorCount;
        _lastCpu = p.TotalProcessorTime; _lastAt = now;
        return wall <= 0 ? 0 : Math.Round(Math.Clamp(used / wall * 100, 0, 100), 1);
    }

    public async Task<Load> ReadAsync(CancellationToken ct)
    {
        var (total, used) = MachineMemory();
        var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "/");
        int conns = -1;
        if (options.Value.IsMulti)
        {
            try
            {
                var control = services.GetRequiredService<ControlStore>();
                await using var c = await control.OpenAsync(ct);
                conns = await c.ExecuteScalarAsync<int>(control.Provider.Dialect.ActiveConnections);
            }
            catch { /* shown as unknown */ }
        }
        ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
        ThreadPool.GetMaxThreads(out var maxWorkers, out _);
        var req = _requests; var err = _errors;
        var load = new Load(Cpu(), total == 0 ? 0 : Math.Round(used * 100.0 / total, 1), used, total, (int)(Process.GetCurrentProcess().WorkingSet64 / 1048576),
            drive.TotalSize == 0 ? 0 : Math.Round(drive.AvailableFreeSpace * 100.0 / drive.TotalSize, 1), drive.AvailableFreeSpace, drive.TotalSize,
            conns, Process.GetCurrentProcess().Threads.Count, maxWorkers - freeWorkers, req - _lastRequests, err - _lastErrors, DateTime.UtcNow);
        _lastRequests = req; _lastErrors = err;
        Current = load;
        return load;
    }

    /// <summary>Total and used physical memory of the machine (Windows: GlobalMemoryStatusEx; Linux: /proc/meminfo).</summary>
    private static (long Total, long Used) MachineMemory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var m = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref m)) return ((long)m.TotalPhys, (long)(m.TotalPhys - m.AvailPhys));
            }
            else if (File.Exists("/proc/meminfo"))
            {
                var info = File.ReadAllLines("/proc/meminfo").Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => long.TryParse(p[1].Trim().Split(' ')[0], out var kb) ? kb * 1024 : 0);
                var total = info.GetValueOrDefault("MemTotal"); var avail = info.GetValueOrDefault("MemAvailable");
                if (total > 0) return (total, total - avail);
            }
        }
        catch { /* fall through */ }
        var gc = GC.GetGCMemoryInfo();
        return (gc.TotalAvailableMemoryBytes, gc.MemoryLoadBytes);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stop);
                var l = await ReadAsync(stop);
                if (!options.Value.IsMulti) continue;
                var control = services.GetRequiredService<ControlStore>();
                await using var c = await control.OpenAsync(stop);
                await control.Query(c).Query("control.server_sample").InsertAsync(new Dictionary<string, object?>
                {
                    ["at_utc"] = l.AtUtc, ["cpu_pct"] = (decimal)l.CpuPct, ["memory_pct"] = (decimal)l.MemoryPct, ["process_mb"] = l.ProcessMb,
                    ["disk_free_pct"] = (decimal)l.DiskFreePct, ["db_connections"] = Math.Max(0, l.DbConnections), ["requests"] = l.RequestsLastMinute, ["errors"] = l.ErrorsLastMinute,
                }, cancellationToken: stop);
                /* Keep 35 days: enough for "same day last week" and a month of history. */
                if (DateTime.UtcNow.Minute == 0)
                    await control.Query(c).Query("control.server_sample").Where("at_utc", "<", DateTime.UtcNow.AddDays(-35)).DeleteAsync(cancellationToken: stop);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not record a server load sample."); }
        }
    }
}
