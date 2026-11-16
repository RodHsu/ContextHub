using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Memory.Infrastructure;

public sealed record RequestArrivalClockDomain(Guid KernelBootId, string TimeNamespace,
    long MonotonicOffsetNs, long BoottimeOffsetNs, string Clocksource);

public sealed record RequestArrivalClockReading(long RawBeforeNs, long RawAfterNs,
    long MonotonicNs, long BoottimeNs, long RealtimeNs);

/// <summary>Optional diagnostic clock only; never replaces the application's TimeProvider.</summary>
public interface IRequestArrivalClock
{
    bool TryReadDomain(out RequestArrivalClockDomain? domain);
    bool TryRead(out RequestArrivalClockReading? reading);
}

public sealed class LinuxRequestArrivalClock : IRequestArrivalClock
{
    public bool TryReadDomain(out RequestArrivalClockDomain? domain)
    {
        domain = null;
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
        try
        {
            var boot = ReadBounded("/proc/sys/kernel/random/boot_id").Trim();
            var name = new FileInfo("/proc/self/ns/time").LinkTarget;
            var source = ReadBounded("/sys/devices/system/clocksource/clocksource0/current_clocksource").Trim();
            var offsets = ReadBounded("/proc/self/timens_offsets").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (!Guid.TryParseExact(boot, "D", out var bootId) || bootId == Guid.Empty ||
                name is null || !Regex.IsMatch(name, @"\Atime:\[[0-9]{1,20}\]\z", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(source, @"\A[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant) || offsets.Length != 2) return false;
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var offset in offsets)
            {
                var parts = offset.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[0] is not ("monotonic" or "boottime") ||
                    !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds) ||
                    !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ns) || ns is < 0 or >= 1_000_000_000 ||
                    !values.TryAdd(parts[0], checked(seconds * 1_000_000_000 + ns))) return false;
            }
            domain = new(bootId, name, values["monotonic"], values["boottime"], source);
            return true;
        }
        catch (Exception) { return false; }
    }

    public bool TryRead(out RequestArrivalClockReading? reading)
    {
        reading = null;
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
        try
        {
            if (!ReadClock(4, out var before) || !ReadClock(1, out var monotonic) ||
                !ReadClock(7, out var boottime) || !ReadClock(0, out var realtime) || !ReadClock(4, out var after) ||
                after < before) return false;
            reading = new(before, after, monotonic, boottime, realtime);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static string ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        var buffer = new char[513];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        if (length > 512) throw new InvalidDataException("Clock domain metadata exceeds its bound.");
        return new string(buffer, 0, length);
    }

    private static bool ReadClock(int clock, out long value)
    {
        value = 0;
        if (clock_gettime(clock, out var stamp) != 0 || stamp.Seconds < 0 || stamp.Nanoseconds is < 0 or >= 1_000_000_000) return false;
        value = checked(stamp.Seconds * 1_000_000_000 + stamp.Nanoseconds);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTimespec { public long Seconds; public long Nanoseconds; }

    [DllImport("libc", SetLastError = true)]
    private static extern int clock_gettime(int clock, out NativeTimespec stamp);
}

public sealed record RequestArrivalClockAnchor(Guid BootId, long Sequence,
    RequestArrivalClockDomain Domain, RequestArrivalClockReading Reading);

public sealed record RequestArrivalClockCoverage(RequestArrivalClockDomain? Domain,
    long? RawStartedBeforeNs, long? RawStartedAfterNs, long RawMaximumSpanNs,
    long Checks, long Failures, long Discontinuities, long Suspends, string? InvalidReason,
    long AnchorsAdmitted, long AnchorsDropped, IReadOnlyList<RequestArrivalClockAnchor> Anchors);
