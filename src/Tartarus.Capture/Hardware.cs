using Microsoft.Win32;
using Vortice.DXGI;

namespace Tartarus.Capture;

/// <summary>
/// The PC's processor and graphics cards, for bug reports (2026-11-06: reports had cores and memory only, so how slow the
/// players' PCs are could only be guessed from scan times). Names only: nothing that tells one PC from another.
/// </summary>
public static class Hardware
{
    public sealed record Gpu(string Name, string Vendor, long MemoryMb, string? Driver);

    public sealed record Info(string? Cpu, int LogicalCores, IReadOnlyList<Gpu> Gpus)
    {
        /// <summary>One line for the log and the tags: "CPU · 16 threads · GPU (12288 MB) + GPU".</summary>
        public override string ToString() =>
            $"{Cpu ?? "CPU unknown"} · {LogicalCores} threads · "
            + (Gpus.Count == 0 ? "no graphics card read" : string.Join(" + ", Gpus.Select(g => $"{g.Name} ({g.MemoryMb} MB{(g.Driver != null ? ", driver " + g.Driver : "")})")));
    }

    /// <summary>
    /// Reads it (a registry value and DXGI's adapters, some ms); never throws. Cards with the most memory first: DXGI lists the
    /// main display's first, which on this PC (Ryzen 7950X + RTX 5090) is the processor's built-in Radeon.
    /// </summary>
    public static Info Read() => new(ReadCpu(), Environment.ProcessorCount, ReadGpus().OrderByDescending(g => g.MemoryMb).ToList());

    private static string? ReadCpu()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() is { Length: > 0 } name ? name : null;
        }
        catch (Exception) { return null; }
    }

    private static List<Gpu> ReadGpus()
    {
        var gpus = new List<Gpu>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                using (adapter)
                {
                    var d = adapter.Description1;
                    // Windows's own software renderer (Microsoft Basic Render Driver) is on every PC.
                    if ((d.Flags & AdapterFlags.Software) != 0 || d.VendorId == 0x1414) continue;
                    // The same card shows once per output it drives on some PCs.
                    if (gpus.Any(g => g.Name == d.Description.Trim())) continue;
                    gpus.Add(new Gpu(d.Description.Trim(), VendorName(d.VendorId), (long)(d.DedicatedVideoMemory / (1024 * 1024)), Driver(adapter)));
                }
        }
        catch (Exception) { }
        return gpus;
    }

    private static string VendorName(uint id) => id switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x5143 => "Qualcomm",
        _ => $"0x{id:X4}",
    };

    /// <summary>The user-mode driver's version, as Device Manager shows it (32.0.15.6094).</summary>
    private static string? Driver(IDXGIAdapter1 adapter)
    {
        try
        {
            if (adapter.CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out long v).Failure) return null;
            return $"{(v >> 48) & 0xFFFF}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
        }
        catch (Exception) { return null; }
    }
}
