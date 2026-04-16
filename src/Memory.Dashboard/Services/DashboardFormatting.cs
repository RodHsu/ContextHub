namespace Memory.Dashboard.Services;

public static class DashboardFormatting
{
    public static string Bytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = value;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024d;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }

    public static string Percent(double value)
        => $"{value:0.##}%";

    public static string Relative(DateTimeOffset value)
    {
        var delta = DateTimeOffset.UtcNow - value;
        if (delta.TotalSeconds < 60)
        {
            return $"{Math.Max((int)delta.TotalSeconds, 0)} 秒前";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes} 分鐘前";
        }

        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours} 小時前";
        }

        return $"{(int)delta.TotalDays} 天前";
    }

    public static string Timestamp(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public static string TimestampWithZone(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss 'GMT'zzz");
}
