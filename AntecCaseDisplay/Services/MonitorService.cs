using System.Diagnostics;

namespace AntecCaseDisplay.Services;

/// <summary>
/// Background worker that polls HWiNFO and drives the Antec display. The GUI
/// owns the lifecycle: Start/Stop/UpdateConfig can be called any time.
/// </summary>
public sealed class MonitorService : IDisposable
{
    public sealed record Status(
        bool HwInfoConnected,
        bool DisplayConnected,
        double? CpuValue,
        double? GpuValue,
        IReadOnlyList<string> CpuMatched,
        IReadOnlyList<string> GpuMatched,
        IReadOnlyList<HwInfoReader.Reading> AllReadings,
        string? LastError);

    public event Action<Status>? StatusChanged;
    public event Action<string, double, double>? AlertFired; // (slotName, value, threshold)
    public event Action<string>? Log;

    private readonly object _lock = new();
    private Config _config;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    private DateTime _lastCpuAlert = DateTime.MinValue;
    private DateTime _lastGpuAlert = DateTime.MinValue;

    public MonitorService(Config initial)
    {
        _config = initial.Clone();
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _runTask is { IsCompleted: false };
            }
        }
    }

    public void UpdateConfig(Config newConfig)
    {
        lock (_lock)
        {
            _config = newConfig.Clone();
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_runTask is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            // A dedicated thread rather than the thread pool, so it can run
            // at raised priority (see ProcessPriorityService) and keeps
            // sending frames when every core is pinned.
            _runTask = Task.Factory.StartNew(() => Run(token), token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    public void Stop()
    {
        Task? task;
        lock (_lock)
        {
            if (_cts is null) return;
            _cts.Cancel();
            task = _runTask;
        }
        try { task?.Wait(2000); } catch { /* swallow on shutdown */ }
        lock (_lock)
        {
            _cts?.Dispose();
            _cts = null;
            _runTask = null;
        }
    }

    private void Run(CancellationToken token)
    {
        using var hw = new HwInfoReader();
        using var display = new AntecDisplay();
        var nextDisplayAttempt = DateTime.MinValue;
        var sinceLastTick = Stopwatch.StartNew();
        var expectedGapMs = 0;

        while (!token.IsCancellationRequested)
        {
            Config cfg;
            lock (_lock) { cfg = _config; }

            var priority = cfg.HighPriority ? ThreadPriority.Highest : ThreadPriority.Normal;
            if (Thread.CurrentThread.Priority != priority) Thread.CurrentThread.Priority = priority;

            // Leave a trail for "display blanked / dashboard froze" reports:
            // a late tick means we weren't scheduled (CPU starved).
            var gapMs = sinceLastTick.ElapsedMilliseconds;
            if (expectedGapMs > 0 && gapMs > expectedGapMs + 2000)
            {
                Log?.Invoke($"Update loop ran {gapMs - expectedGapMs} ms late (CPU starved?).");
            }
            sinceLastTick.Restart();

            if (!hw.IsOpen && !hw.TryOpen())
            {
                EmitStatus(false, display.IsOpen, null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<HwInfoReader.Reading>(),
                    "HWiNFO shared memory not available. Is HWiNFO64 running with 'Shared Memory Support' enabled?");
                expectedGapMs = Wait(cfg.ReconnectIntervalMs, token);
                continue;
            }

            IReadOnlyList<HwInfoReader.Reading> readings;
            try
            {
                readings = hw.ReadAll();
            }
            catch (Exception ex)
            {
                var error = $"HWiNFO read failed: {ex.Message}";
                Log?.Invoke(error);
                hw.Close();
                EmitStatus(false, display.IsOpen, null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<HwInfoReader.Reading>(), error);
                expectedGapMs = Wait(cfg.ReconnectIntervalMs, token);
                continue;
            }

            var cpu = SensorResolver.Resolve(cfg.Cpu, readings);
            var gpu = SensorResolver.Resolve(cfg.Gpu, readings);
            string? lastError = null;

            // The case display is optional for everything else that consumes
            // readings (dashboard, alerts, tray tooltip), so keep polling HWiNFO
            // while it's missing and only retry the HID enumeration every
            // reconnect interval.
            if (!display.IsOpen && DateTime.UtcNow >= nextDisplayAttempt && !display.TryOpen())
            {
                nextDisplayAttempt = DateTime.UtcNow.AddMilliseconds(cfg.ReconnectIntervalMs);
            }

            if (display.IsOpen)
            {
                try
                {
                    display.Send(
                        ApplyDisplayRounding(cpu.Value, cfg.IntegerTemperatures),
                        ApplyDisplayRounding(gpu.Value, cfg.IntegerTemperatures));
                }
                catch (Exception ex)
                {
                    lastError = $"Display write failed: {ex.Message}";
                    Log?.Invoke(lastError);
                    display.Close();
                    nextDisplayAttempt = DateTime.UtcNow.AddMilliseconds(cfg.ReconnectIntervalMs);
                }
            }
            else
            {
                lastError = $"Antec Flux Pro display not found (VID=0x{AntecDisplay.VendorId:X4}, PID=0x{AntecDisplay.ProductId:X4}).";
            }

            CheckAlerts(cfg, cpu.Value, gpu.Value);

            if (cfg.Verbose)
            {
                Log?.Invoke($"CPU={Format(cpu.Value)} GPU={Format(gpu.Value)} (cpu matches: {string.Join(", ", cpu.MatchedNames)}; gpu matches: {string.Join(", ", gpu.MatchedNames)})");
            }

            EmitStatus(true, display.IsOpen, cpu.Value, gpu.Value, cpu.MatchedNames, gpu.MatchedNames, readings, lastError);

            expectedGapMs = Wait(cfg.UpdateIntervalMs, token);
        }
    }

    private void CheckAlerts(Config cfg, double? cpu, double? gpu)
    {
        if (!cfg.AlertsEnabled) return;
        var now = DateTime.UtcNow;
        var minGap = TimeSpan.FromSeconds(Math.Max(1, cfg.AlertMinIntervalSeconds));

        if (cpu is { } c && cfg.Cpu.AlertThreshold is { } ct && c >= ct && now - _lastCpuAlert >= minGap)
        {
            _lastCpuAlert = now;
            AlertFired?.Invoke("CPU", c, ct);
        }
        if (gpu is { } g && cfg.Gpu.AlertThreshold is { } gt && g >= gt && now - _lastGpuAlert >= minGap)
        {
            _lastGpuAlert = now;
            AlertFired?.Invoke("GPU", g, gt);
        }
    }

    private void EmitStatus(
        bool hwOk, bool dispOk,
        double? cpu, double? gpu,
        IReadOnlyList<string> cpuMatched, IReadOnlyList<string> gpuMatched,
        IReadOnlyList<HwInfoReader.Reading> all,
        string? error)
    {
        try
        {
            StatusChanged?.Invoke(new Status(hwOk, dispOk, cpu, gpu, cpuMatched, gpuMatched, all, error));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StatusChanged handler threw: {ex}");
        }
    }

    private static double? ApplyDisplayRounding(double? value, bool integer)
    {
        if (value is null) return null;
        return integer ? Math.Round(value.Value, 0, MidpointRounding.AwayFromZero) : value;
    }

    /// <summary>Sleeps on this thread (returns early on stop) and returns
    /// the delay used. Deliberately not Task.Delay: its continuation would
    /// resume on a normal-priority thread-pool thread.</summary>
    private static int Wait(int ms, CancellationToken ct)
    {
        ms = Math.Max(50, ms);
        ct.WaitHandle.WaitOne(ms);
        return ms;
    }

    private static string Format(double? v) => v is null ? "--" : v.Value.ToString("F1");

    public void Dispose() => Stop();
}
