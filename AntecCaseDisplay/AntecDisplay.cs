using HidSharp;

namespace AntecCaseDisplay;

/// <summary>
/// Sends CPU/GPU temperature frames to the Antec Flux Pro case LCD over USB HID.
///
/// Wire format (12 bytes, no report ID in the protocol itself):
///   [0] 0x55
///   [1] 0xAA
///   [2] 0x01
///   [3] 0x01
///   [4] 0x06
///   [5] CPU tens digit   (e.g. 24.7 -> 2)
///   [6] CPU ones digit   (e.g. 24.7 -> 4)
///   [7] CPU tenths digit (e.g. 24.7 -> 7)
///   [8] GPU tens digit
///   [9] GPU ones digit
///  [10] GPU tenths digit
///  [11] Checksum = (sum of bytes [0..10]) mod 256
///
/// On Windows the HID stack expects a leading report ID byte (0x00), so we
/// write 13 bytes total.
/// </summary>
public sealed class AntecDisplay : IDisposable
{
    public const int VendorId = 0x2022;
    public const int ProductId = 0x0522;

    private static readonly byte[] Header = { 0x55, 0xAA, 0x01, 0x01, 0x06 };
    private static readonly byte[] MissingTempBytes = { 0xEE, 0xEE, 0xEE };

    private HidDevice? _device;
    private HidStream? _stream;

    public bool IsOpen => _stream is not null;

    public bool TryOpen()
    {
        Close();

        var device = DeviceList.Local
            .GetHidDevices(VendorId, ProductId)
            .FirstOrDefault();

        if (device is null)
        {
            return false;
        }

        if (!device.TryOpen(out var stream))
        {
            return false;
        }

        stream.WriteTimeout = 1000;
        _device = device;
        _stream = stream;
        return true;
    }

    public void Send(double? cpuTempC, double? gpuTempC)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Device not open. Call TryOpen() first.");
        }

        var packet = BuildPacket(cpuTempC, gpuTempC);

        // HidSharp expects the first byte of the write buffer to be the report ID.
        var hidFrame = new byte[packet.Length + 1];
        hidFrame[0] = 0x00;
        Buffer.BlockCopy(packet, 0, hidFrame, 1, packet.Length);

        _stream.Write(hidFrame);
    }

    internal static byte[] BuildPacket(double? cpuTempC, double? gpuTempC)
    {
        var packet = new byte[12];
        Buffer.BlockCopy(Header, 0, packet, 0, Header.Length);

        var cpuBytes = EncodeTemperature(cpuTempC);
        var gpuBytes = EncodeTemperature(gpuTempC);
        Buffer.BlockCopy(cpuBytes, 0, packet, 5, 3);
        Buffer.BlockCopy(gpuBytes, 0, packet, 8, 3);

        int sum = 0;
        for (int i = 0; i < 11; i++) sum += packet[i];
        packet[11] = (byte)(sum & 0xFF);

        return packet;
    }

    /// <summary>
    /// Splits a temperature into (tens, ones, tenths) digits. Values outside the
    /// 0..99.9°C range are clamped; a null value produces the "missing data"
    /// sentinel that the display renders as dashes.
    /// </summary>
    internal static byte[] EncodeTemperature(double? tempC)
    {
        if (tempC is null || double.IsNaN(tempC.Value) || double.IsInfinity(tempC.Value))
        {
            return (byte[])MissingTempBytes.Clone();
        }

        var clamped = Math.Clamp(tempC.Value, 0.0, 99.9);
        var tenths = (int)Math.Round(clamped * 10.0, MidpointRounding.AwayFromZero);
        // Round may push 99.9 -> 1000 tenths if the input exceeds 99.949; clamp again.
        if (tenths > 999) tenths = 999;

        byte tens = (byte)(tenths / 100);
        byte ones = (byte)((tenths / 10) % 10);
        byte dec = (byte)(tenths % 10);
        return new[] { tens, ones, dec };
    }

    public void Close()
    {
        _stream?.Dispose();
        _stream = null;
        _device = null;
    }

    public void Dispose() => Close();
}
