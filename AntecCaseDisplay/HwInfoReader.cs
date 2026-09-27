using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace AntecCaseDisplay;

/// <summary>
/// Reads sensor readings from HWiNFO64's shared memory region.
/// Format reference: https://gist.github.com/namazso/0c37be5a53863954c8c8279f66cfb1cc
/// </summary>
public sealed class HwInfoReader : IDisposable
{
    private const string SharedMemoryName = @"Global\HWiNFO_SENS_SM2";
    // HWiNFO writes the four bytes 'H','W','i','S'. On little-endian x86 that
    // reads back as the uint32 0x53695748 (matches HWiNFO's own SDK constant).
    private const uint ExpectedMagic = 0x53695748;

    // Header layout (little-endian)
    private const int OffsetMagic = 0x00;
    private const int OffsetSensorSectionOffset = 0x14;
    private const int OffsetSensorElementSize = 0x18;
    private const int OffsetSensorElementCount = 0x1C;
    private const int OffsetEntrySectionOffset = 0x20;
    private const int OffsetEntryElementSize = 0x24;
    private const int OffsetEntryElementCount = 0x28;
    private const int HeaderSize = 0x2C;

    // Sensor (device) element layout
    private const int SensorOffsetOriginalName = 0x0008;
    private const int MinSensorElementSize = SensorOffsetOriginalName + NameFieldLength;

    // Reading entry layout
    private const int EntryOffsetType = 0x0000;
    private const int EntryOffsetSensorIndex = 0x0004;
    private const int EntryOffsetOriginalName = 0x000C;
    private const int EntryOffsetUserName = 0x008C;
    private const int EntryOffsetUnit = 0x010C;
    private const int EntryOffsetValue = 0x011C;
    private const int EntryOffsetValueMin = 0x0124;
    private const int EntryOffsetValueMax = 0x012C;
    private const int MinEntryElementSize = EntryOffsetValueMax + sizeof(double);
    private const int NameFieldLength = 128;
    private const int UnitFieldLength = 16;

    // HWiNFO stores strings in the system's ANSI code page ("°C" is 0xB0 0x43
    // on Western systems, which isn't valid UTF-8).
    private static readonly Encoding AnsiEncoding = CreateAnsiEncoding();

    public enum SensorType : uint
    {
        None = 0,
        Temperature = 1,
        Voltage = 2,
        Fan = 3,
        Current = 4,
        Power = 5,
        Clock = 6,
        Usage = 7,
        Other = 8,
    }

    /// <param name="SensorName">Original name of the sensor (device) the reading belongs to, e.g. "GPU [#0]: NVIDIA GeForce RTX 4080".</param>
    /// <param name="Min">Lowest value HWiNFO has seen since its counters were last reset.</param>
    /// <param name="Max">Highest value HWiNFO has seen since its counters were last reset.</param>
    public readonly record struct Reading(
        SensorType Type,
        string SensorName,
        string OriginalName,
        string UserName,
        string Unit,
        double Value,
        double Min,
        double Max);

    private MemoryMappedFile? _mmf;

    // Reused between ticks: the whole region is copied in with one call rather
    // than issuing several accessor reads (each with its own bounds check and
    // handle ref-count) per reading.
    private byte[] _buffer = Array.Empty<byte>();

    public bool IsOpen => _mmf is not null;

    /// <summary>
    /// Attempts to open the HWiNFO shared memory. Returns false if HWiNFO isn't
    /// running or the Shared Memory Support option is disabled.
    /// </summary>
    public bool TryOpen()
    {
        Close();
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(
                SharedMemoryName,
                MemoryMappedFileRights.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Happens when HWiNFO runs elevated and we do not. Caller will surface this.
            return false;
        }
    }

    public IReadOnlyList<Reading> ReadAll()
    {
        if (_mmf is null)
        {
            throw new InvalidOperationException("Shared memory not open. Call TryOpen() first.");
        }

        using var accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        var magic = accessor.ReadUInt32(OffsetMagic);
        if (magic != ExpectedMagic)
        {
            throw new InvalidDataException(
                $"Unexpected HWiNFO shared memory magic 0x{magic:X8}. HWiNFO format may have changed.");
        }

        long needed = RequiredLength(
            accessor.ReadUInt32(OffsetSensorSectionOffset),
            accessor.ReadUInt32(OffsetSensorElementSize),
            accessor.ReadUInt32(OffsetSensorElementCount),
            accessor.ReadUInt32(OffsetEntrySectionOffset),
            accessor.ReadUInt32(OffsetEntryElementSize),
            accessor.ReadUInt32(OffsetEntryElementCount));
        if (needed > accessor.Capacity)
        {
            throw new InvalidDataException(
                $"HWiNFO shared memory header describes {needed} bytes but only {accessor.Capacity} are mapped.");
        }

        if (_buffer.Length < needed) _buffer = new byte[needed];
        accessor.ReadArray(0, _buffer, 0, (int)needed);
        return Parse(_buffer.AsSpan(0, (int)needed));
    }

    /// <summary>Parses a copy of the shared memory region (header included).</summary>
    internal static List<Reading> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != ExpectedMagic)
        {
            throw new InvalidDataException("Not an HWiNFO shared memory snapshot.");
        }

        long sensorOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(OffsetSensorSectionOffset));
        long sensorSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(OffsetSensorElementSize));
        long sensorCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(OffsetSensorElementCount));
        long entryOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(OffsetEntrySectionOffset));
        long entrySize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(OffsetEntryElementSize));
        long entryCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(OffsetEntryElementCount));

        if (RequiredLength(sensorOffset, sensorSize, sensorCount, entryOffset, entrySize, entryCount) > data.Length)
        {
            throw new InvalidDataException("HWiNFO shared memory snapshot is truncated.");
        }

        var sensorNames = new string[sensorCount];
        for (int i = 0; i < sensorCount; i++)
        {
            var sensor = data.Slice((int)(sensorOffset + i * sensorSize), (int)sensorSize);
            sensorNames[i] = DecodeCString(sensor.Slice(SensorOffsetOriginalName, NameFieldLength));
        }

        var results = new List<Reading>((int)entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            var entry = data.Slice((int)(entryOffset + i * entrySize), (int)entrySize);

            var type = (SensorType)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(EntryOffsetType));
            var sensorIndex = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(EntryOffsetSensorIndex));

            results.Add(new Reading(
                type,
                sensorIndex < sensorNames.Length ? sensorNames[sensorIndex] : "",
                DecodeCString(entry.Slice(EntryOffsetOriginalName, NameFieldLength)),
                DecodeCString(entry.Slice(EntryOffsetUserName, NameFieldLength)),
                DecodeCString(entry.Slice(EntryOffsetUnit, UnitFieldLength)),
                BinaryPrimitives.ReadDoubleLittleEndian(entry.Slice(EntryOffsetValue)),
                BinaryPrimitives.ReadDoubleLittleEndian(entry.Slice(EntryOffsetValueMin)),
                BinaryPrimitives.ReadDoubleLittleEndian(entry.Slice(EntryOffsetValueMax))));
        }

        return results;
    }

    /// <summary>Validates the element sizes and returns how many bytes the
    /// header says the sensor and entry sections span.</summary>
    private static long RequiredLength(
        long sensorOffset, long sensorSize, long sensorCount,
        long entryOffset, long entrySize, long entryCount)
    {
        if (entrySize < MinEntryElementSize || (sensorCount > 0 && sensorSize < MinSensorElementSize))
        {
            throw new InvalidDataException(
                $"Unexpected HWiNFO element sizes (sensor {sensorSize}, entry {entrySize}). HWiNFO format may have changed.");
        }

        long needed = Math.Max(HeaderSize, Math.Max(
            sensorOffset + sensorSize * sensorCount,
            entryOffset + entrySize * entryCount));
        if (needed > int.MaxValue)
        {
            throw new InvalidDataException($"HWiNFO shared memory header describes an implausible {needed} bytes.");
        }
        return needed;
    }

    /// <summary>
    /// Finds the first reading whose type matches <paramref name="type"/> and whose
    /// original or user name matches <paramref name="pattern"/> (regex, case-insensitive).
    /// </summary>
    public static Reading? FindByPattern(
        IReadOnlyList<Reading> readings,
        SensorType type,
        string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var r in readings)
        {
            if (r.Type != type) continue;
            if (regex.IsMatch(r.OriginalName) || regex.IsMatch(r.UserName))
            {
                return r;
            }
        }
        return null;
    }

    private static string DecodeCString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end >= 0) field = field.Slice(0, end);

        // Plain ASCII (valid UTF-8) covers nearly every name; only units like
        // "°C" need the code page.
        var encoding = Utf8.IsValid(field) ? Encoding.UTF8 : AnsiEncoding;
        return encoding.GetString(field).Trim();
    }

    private static Encoding CreateAnsiEncoding()
    {
        try
        {
            var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            if (codePage != 65001 &&
                CodePagesEncodingProvider.Instance.GetEncoding(codePage) is { } ansi)
            {
                return ansi;
            }
        }
        catch
        {
            // Fall through to Latin-1, which matches Windows-1252 for "°".
        }
        return Encoding.Latin1;
    }

    public void Close()
    {
        _mmf?.Dispose();
        _mmf = null;
    }

    public void Dispose() => Close();
}
