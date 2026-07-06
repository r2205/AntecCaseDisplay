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

    // The panel firmware goes dark a couple of seconds after the last frame it
    // received, so while HWiNFO is unavailable we keep re-sending the "missing
    // data" dashes at this cadence to keep them visible.
    private const int BlankKeepAliveIntervalMs = 1000;

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
            _runTask = Task.Run(() => RunAsync(token), token);
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

    private async Task RunAsync(CancellationToken token)
    {
        using var hw = new HwInfoReader();
        using var display = new AntecDisplay();

        while (!token.IsCancellationRequested)
        {
            Config cfg;
            lock (_lock) { cfg = _config; }

            try
            {
                await TickAsync(hw, display, cfg, token);
            }
            catch (OperationCanceledException)
            {
                // Stop() in progress; the loop condition takes care of the exit.
            }
            catch (Exception ex)
            {
                // Nothing may kill the worker silently: a faulted task would
                // freeze the tooltip and display at their last values, with the
                // error surfacing only if the finalizer ever runs. Report it,
                // reset the connections, and keep the loop alive.
                var error = $"Monitor error: {ex.Message}";
                Log?.Invoke(error);
                hw.Close();
                TrySendBlank(display);
                EmitStatus(false, display.IsOpen, null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<HwInfoReader.Reading>(), error);
                await Delay(cfg.ReconnectIntervalMs, token);
            }
        }
    }

    private async Task TickAsync(HwInfoReader hw, AntecDisplay display, Config cfg, CancellationToken token)
    {
        if (!hw.IsOpen && !hw.TryOpen())
        {
            var error = "HWiNFO shared memory not available. Is HWiNFO64 running with 'Shared Memory Support' enabled?";
            // Normally the branch below opens the display; do it here too so
            // the panel can show dashes rather than sit dark while HWiNFO is away.
            if (!display.IsOpen) display.TryOpen();
            EmitStatus(false, display.IsOpen, null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<HwInfoReader.Reading>(), error);
            await DelayWithBlankFrames(display, cfg.ReconnectIntervalMs, token);
            return;
        }

        if (!display.IsOpen && !display.TryOpen())
        {
            var error = $"Antec Flux Pro display not found (VID=0x{AntecDisplay.VendorId:X4}, PID=0x{AntecDisplay.ProductId:X4}).";
            EmitStatus(true, false, null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<HwInfoReader.Reading>(), error);
            await Delay(cfg.ReconnectIntervalMs, token);
            return;
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
            await DelayWithBlankFrames(display, cfg.ReconnectIntervalMs, token);
            return;
        }

        var cpu = SensorResolver.Resolve(cfg.Cpu, readings);
        var gpu = SensorResolver.Resolve(cfg.Gpu, readings);

        var cpuToSend = ApplyDisplayRounding(cpu.Value, cfg.IntegerTemperatures);
        var gpuToSend = ApplyDisplayRounding(gpu.Value, cfg.IntegerTemperatures);

        try
        {
            display.Send(cpuToSend, gpuToSend);
        }
        catch (Exception ex)
        {
            var error = $"Display write failed: {ex.Message}";
            Log?.Invoke(error);
            display.Close();
            EmitStatus(true, false, cpu.Value, gpu.Value, cpu.MatchedNames, gpu.MatchedNames, readings, error);
            await Delay(cfg.ReconnectIntervalMs, token);
            return;
        }

        CheckAlerts(cfg, cpu.Value, gpu.Value);

        if (cfg.Verbose)
        {
            Log?.Invoke($"CPU={Format(cpu.Value)} GPU={Format(gpu.Value)} (cpu matches: {string.Join(", ", cpu.MatchedNames)}; gpu matches: {string.Join(", ", gpu.MatchedNames)})");
        }

        EmitStatus(true, true, cpu.Value, gpu.Value, cpu.MatchedNames, gpu.MatchedNames, readings, null);

        await Delay(cfg.UpdateIntervalMs, token);
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

    /// <summary>
    /// Waits like <see cref="Delay"/>, but re-sends the "missing data" dashes once
    /// a second so the panel keeps showing them instead of going dark while we
    /// wait to reconnect.
    /// </summary>
    private static async Task DelayWithBlankFrames(AntecDisplay display, int totalMs, CancellationToken token)
    {
        var remaining = Math.Max(50, totalMs);
        while (remaining > 0 && !token.IsCancellationRequested)
        {
            TrySendBlank(display);
            var chunk = Math.Min(BlankKeepAliveIntervalMs, remaining);
            await Delay(chunk, token);
            remaining -= chunk;
        }
    }

    private static void TrySendBlank(AntecDisplay display)
    {
        if (!display.IsOpen) return;
        try
        {
            display.Send(null, null);
        }
        catch
        {
            // If even the blank frame fails, close and let the main loop's
            // display reconnect path deal with it.
            display.Close();
        }
    }

    private static double? ApplyDisplayRounding(double? value, bool integer)
    {
        if (value is null) return null;
        return integer ? Math.Round(value.Value, 0, MidpointRounding.AwayFromZero) : value;
    }

    private static async Task Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(Math.Max(50, ms), ct); }
        catch (TaskCanceledException) { /* expected on stop */ }
    }

    private static string Format(double? v) => v is null ? "--" : v.Value.ToString("F1");

    public void Dispose() => Stop();
}
