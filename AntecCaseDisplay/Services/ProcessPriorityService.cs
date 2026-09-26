using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AntecCaseDisplay.Services;

/// <summary>
/// Keeps the app responsive when a game or stress test pins every core.
/// At normal priority our threads rarely get scheduled then, so the case
/// display stops receiving frames (its firmware blanks it) and the dashboard
/// freezes. The app needs well under 1% CPU, so running it above normal costs
/// the foreground workload essentially nothing.
/// </summary>
public static class ProcessPriorityService
{
    public static void Apply(bool high, Action<string>? log = null)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = high ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not change process priority: {ex.Message}");
        }

        SetPowerThrottling(allowed: !high, log);
    }

    // Windows 11 puts background apps with no visible window into "efficiency
    // mode" (EcoQoS): low clocks and, on hybrid CPUs, efficiency cores — the
    // first to be saturated under an all-core load. Opt out while `high`.
    private static void SetPowerThrottling(bool allowed, Action<string>? log)
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            // ControlMask = the policies we take charge of; StateMask = which
            // of those are on. Both zero hands control back to Windows.
            ControlMask = allowed ? 0 : ProcessPowerThrottlingExecutionSpeed,
            StateMask = 0,
        };
        try
        {
            if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                    ref state, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
            {
                // Not supported before Windows 10 1709; harmless to skip.
                log?.Invoke($"Could not change power throttling (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Pre-Windows 8: no SetProcessInformation at all.
        }
    }

    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess, int processInformationClass,
        ref ProcessPowerThrottlingState processInformation, uint processInformationSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
