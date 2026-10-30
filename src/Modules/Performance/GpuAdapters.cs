using Microsoft.Win32;

namespace WinScope.Modules.Performance;

/// <summary>
/// Names and video-memory sizes of the graphics adapters, keyed by the LUID used in the GPU counter names.
/// Windows' DirectX graphics kernel keeps one subkey per adapter under HKLM\SOFTWARE\Microsoft\DirectX with
/// "Description" (REG_SZ), "AdapterLuid" (REG_QWORD: LUID.LowPart in the low 32 bits, HighPart in the high 32)
/// and "DedicatedVideoMemory" (REG_QWORD, bytes – unlike WMI this isn't capped at 4 GB). Read-only, readable by
/// standard users; called on the sampler thread, never the UI thread.
/// </summary>
internal static class GpuAdapters
{
    public readonly record struct Info(string Name, long DedicatedBytes);

    public static Dictionary<AdapterKey, Info> Read()
    {
        var map = new Dictionary<AdapterKey, Info>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX", false);
            if (root == null) return map;
            int n = 0;
            foreach (var sub in root.GetSubKeyNames())
            {
                if (++n > 64) break;
                try
                {
                    using var k = root.OpenSubKey(sub, false);
                    if (k?.GetValue("AdapterLuid") is not long luid) continue;
                    string name = (k.GetValue("Description") as string)?.Trim() ?? "";
                    long vram = k.GetValue("DedicatedVideoMemory") is long v && v > 0 ? v : 0;
                    if (name.Length > 120) name = name[..120];
                    var key = new AdapterKey((uint)((ulong)luid >> 32), (uint)((ulong)luid & 0xFFFFFFFF));
                    // Entries for removed adapters linger; LUIDs are per boot, so stale ones simply won't match.
                    if (!map.TryGetValue(key, out var old) || vram > old.DedicatedBytes) map[key] = new Info(name, vram);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return map;
    }
}
