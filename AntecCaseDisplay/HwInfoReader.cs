using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace AntecCaseDisplay;

/// <summary>
/// Reads sensor readings from HWiNFO64's shared memory region.
/// Format reference: https://gist.github.com/namazso/0c37be5a53863954c8c8279f66cfb1cc
/// </summary>
public sealed class HwInfoReader : IDisposable
{
    private const string SharedMemoryName = @"Global\HWiNFO_SENS_SM2";
    private const uint ExpectedMagic = 0x48695753; // 'SiWH'

    // Header layout (little-endian)
    private const int OffsetMagic = 0x00;
    private const int OffsetSensorSectionOffset = 0x14;
    private const int OffsetSensorElementSize = 0x18;
    private const int OffsetSensorElementCount = 0x1C;
    private const int OffsetEntrySectionOffset = 0x20;
    private const int OffsetEntryElementSize = 0x24;
    private const int OffsetEntryElementCount = 0x28;

    // Reading entry layout
    private const int EntryOffsetType = 0x0000;
    private const int EntryOffsetOriginalName = 0x000C;
    private const int EntryOffsetUserName = 0x008C;
    private const int EntryOffsetValue = 0x011C;
    private const int NameFieldLength = 128;

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

    public readonly record struct Reading(
        SensorType Type,
        string OriginalName,
        string UserName,
        double Value);

    private MemoryMappedFile? _mmf;

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

        var entryOffset = accessor.ReadUInt32(OffsetEntrySectionOffset);
        var entrySize = accessor.ReadUInt32(OffsetEntryElementSize);
        var entryCount = accessor.ReadUInt32(OffsetEntryElementCount);

        var results = new List<Reading>((int)entryCount);
        var nameBuffer = new byte[NameFieldLength];

        for (uint i = 0; i < entryCount; i++)
        {
            long baseAddr = entryOffset + (long)i * entrySize;

            var type = (SensorType)accessor.ReadUInt32(baseAddr + EntryOffsetType);

            accessor.ReadArray(baseAddr + EntryOffsetOriginalName, nameBuffer, 0, NameFieldLength);
            var originalName = DecodeCString(nameBuffer);

            accessor.ReadArray(baseAddr + EntryOffsetUserName, nameBuffer, 0, NameFieldLength);
            var userName = DecodeCString(nameBuffer);

            var value = accessor.ReadDouble(baseAddr + EntryOffsetValue);

            results.Add(new Reading(type, originalName, userName, value));
        }

        return results;
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

    private static string DecodeCString(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        // HWiNFO stores names in the system's ANSI code page. UTF-8 works for the
        // ASCII subset, which covers every sensor name we have ever seen.
        return Encoding.UTF8.GetString(buffer, 0, end).Trim();
    }

    public void Close()
    {
        _mmf?.Dispose();
        _mmf = null;
    }

    public void Dispose() => Close();
}
