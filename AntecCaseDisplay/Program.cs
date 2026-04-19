using AntecCaseDisplay;

var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var config = Config.Load(configPath);

using var hw = new HwInfoReader();
using var display = new AntecDisplay();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine();
    Console.WriteLine("Shutdown requested...");
};

Console.WriteLine("AntecCaseDisplay — HWiNFO64 -> Antec Flux Pro LCD");
Console.WriteLine($"Config: {configPath}");
Console.WriteLine($"CPU sensor pattern: {config.CpuSensorPattern}");
Console.WriteLine($"GPU sensor pattern: {config.GpuSensorPattern}");
Console.WriteLine($"Update interval:    {config.UpdateIntervalMs} ms");
Console.WriteLine("Press Ctrl+C to exit.");
Console.WriteLine();

bool listedSensors = false;

try
{
    while (!cts.IsCancellationRequested)
    {
        if (!hw.IsOpen && !hw.TryOpen())
        {
            Console.WriteLine("[HWiNFO] Shared memory not available. Is HWiNFO64 running with 'Shared Memory Support' enabled?");
            await DelayUntil(config.ReconnectIntervalMs, cts.Token);
            continue;
        }

        if (!display.IsOpen && !display.TryOpen())
        {
            Console.WriteLine($"[Display] Antec Flux Pro display not found (VID=0x{AntecDisplay.VendorId:X4}, PID=0x{AntecDisplay.ProductId:X4}). Make sure the internal USB cable is connected.");
            await DelayUntil(config.ReconnectIntervalMs, cts.Token);
            continue;
        }

        IReadOnlyList<HwInfoReader.Reading> readings;
        try
        {
            readings = hw.ReadAll();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HWiNFO] Read failed: {ex.Message}. Reconnecting...");
            hw.Close();
            await DelayUntil(config.ReconnectIntervalMs, cts.Token);
            continue;
        }

        if (config.ListSensorsOnStart && !listedSensors)
        {
            Console.WriteLine("Temperature sensors reported by HWiNFO:");
            foreach (var r in readings.Where(r => r.Type == HwInfoReader.SensorType.Temperature))
            {
                var label = string.IsNullOrEmpty(r.UserName) ? r.OriginalName : $"{r.OriginalName} (user: {r.UserName})";
                Console.WriteLine($"  {r.Value,6:F1} °C  {label}");
            }
            Console.WriteLine();
            listedSensors = true;
        }

        var cpu = HwInfoReader.FindByPattern(readings, HwInfoReader.SensorType.Temperature, config.CpuSensorPattern);
        var gpu = HwInfoReader.FindByPattern(readings, HwInfoReader.SensorType.Temperature, config.GpuSensorPattern);

        var cpuTemp = cpu?.Value;
        var gpuTemp = gpu?.Value;

        try
        {
            display.Send(cpuTemp, gpuTemp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Display] Write failed: {ex.Message}. Reconnecting...");
            display.Close();
            await DelayUntil(config.ReconnectIntervalMs, cts.Token);
            continue;
        }

        if (config.Verbose)
        {
            Console.WriteLine(
                $"CPU={Format(cpuTemp)}  GPU={Format(gpuTemp)}   " +
                $"[CPU matched: {cpu?.OriginalName ?? "<none>"}] " +
                $"[GPU matched: {gpu?.OriginalName ?? "<none>"}]");
        }

        await DelayUntil(config.UpdateIntervalMs, cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Normal Ctrl+C path
}

Console.WriteLine("Stopped.");

static async Task DelayUntil(int ms, CancellationToken ct)
{
    try
    {
        await Task.Delay(ms, ct);
    }
    catch (TaskCanceledException)
    {
        // Propagate cancellation to the main loop's check.
    }
}

static string Format(double? t) => t is null ? "--.-" : t.Value.ToString("F1");
