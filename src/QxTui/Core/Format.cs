using System.Globalization;

namespace QxTui.Core;

/// <summary>Byte/number formatting shared by apps (was copy-pasted per sample).</summary>
public static class Format
{
    /// <summary>1024-based human bytes: 720KiB, 8.2MiB, 1.4GiB.</summary>
    public static string HumanBytes(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return $"{bytes.ToString(bytes >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture)}{units[u]}";
    }

    /// <summary>btop-style compact from KiB: 720M, 8.2M, 512K, 1.4G.</summary>
    public static string CompactKiB(long kb) => kb switch
    {
        >= 10 * 1024 * 1024 => $"{kb / (1024.0 * 1024.0):F1}G",
        >= 10 * 1024 => $"{kb / 1024.0:F0}M",
        >= 1024 => $"{kb / 1024.0:F1}M",
        _ => $"{kb}K",
    };
}
