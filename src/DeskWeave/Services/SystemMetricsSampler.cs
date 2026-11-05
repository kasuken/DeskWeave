using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DeskWeave.Core;
using static DeskWeave.Interop.NativeMethods;

namespace DeskWeave.Services;

public sealed record MemorySample(long Used, long Total, double Percent);

public sealed record DiskSample(string Path, long Total, long Used, long Available, double Percent, double? ReadRate, double? WriteRate);

public sealed record NetworkSample(
    bool Connected,
    string Kind,
    string Name,
    string Interface,
    string Address,
    int? Signal,
    double? Download,
    double? Upload);

public sealed record SystemSample(
    double? Cpu,
    int Threads,
    MemorySample? Memory,
    DiskSample? Disk,
    double? CpuTemperature,
    double? SsdTemperature,
    NetworkSample Network);

/// <summary>
/// Local-only telemetry sampler. Unsupported or unreadable values stay null and render as N/A.
/// Not thread-safe: call <see cref="Sample"/> from a single background loop.
/// </summary>
public sealed partial class SystemMetricsSampler : IDisposable
{
    private static readonly TimeSpan ConnectionRefresh = TimeSpan.FromSeconds(10);

    private readonly string _diskPath = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private CpuTimes? _previousCpu;
    private (long Received, long Sent)? _previousNetwork;
    private string _previousInterface = string.Empty;
    private TimeSpan _previousTime;

    private PerformanceCounter? _diskRead;
    private PerformanceCounter? _diskWrite;
    private bool _diskPrimed;
    private List<PerformanceCounter> _thermalZones = [];
    private bool _sensorsOpened;

    private (string Ssid, int? Signal) _wifi = (string.Empty, null);
    private TimeSpan _wifiCheckedAt = TimeSpan.MinValue;

    public SystemSample Sample()
    {
        var now = _clock.Elapsed;
        var elapsed = _previousTime == TimeSpan.Zero ? TimeSpan.Zero : now - _previousTime;
        if (!_sensorsOpened)
        {
            // Performance counters are slow to open, so this happens once, on the sampling thread.
            OpenSensors();
            _sensorsOpened = true;
        }

        var currentCpu = ReadCpuTimes();
        var cpu = Rates.CpuUsage(_previousCpu, currentCpu);
        _previousCpu = currentCpu;

        var network = SampleNetwork(now, elapsed);
        _previousTime = now;

        return new SystemSample(cpu, Environment.ProcessorCount, ReadMemory(), ReadDisk(), ReadCpuTemperature(), null, network);
    }

    private static CpuTimes? ReadCpuTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? new CpuTimes(idle, kernel, user) : null;

    private static MemorySample? ReadMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
        {
            return null;
        }

        var used = (long)(status.ullTotalPhys - status.ullAvailPhys);
        var total = (long)status.ullTotalPhys;
        return new MemorySample(used, total, Math.Round(100.0 * used / total, 1));
    }

    private DiskSample? ReadDisk()
    {
        try
        {
            var drive = new DriveInfo(_diskPath);
            if (!drive.IsReady)
            {
                return null;
            }

            var total = drive.TotalSize;
            var used = total - drive.TotalFreeSpace;
            var available = drive.AvailableFreeSpace;
            var denominator = used + available;
            var percent = denominator > 0 ? Math.Round(100.0 * used / denominator, 1) : 0;

            double? read = null, write = null;
            if (_diskRead is not null && _diskWrite is not null)
            {
                // Rate counters need two samples; the first reading is always zero.
                var r = _diskRead.NextValue();
                var w = _diskWrite.NextValue();
                if (_diskPrimed)
                {
                    read = Math.Round(r);
                    write = Math.Round(w);
                }

                _diskPrimed = true;
            }

            return new DiskSample(_diskPath.TrimEnd('\\'), total, used, available, percent, read, write);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private double? ReadCpuTemperature()
    {
        double? hottest = null;
        foreach (var zone in _thermalZones)
        {
            try
            {
                // Thermal Zone Information reports Kelvin; ignore placeholders and implausible values.
                var celsius = zone.NextValue() - 273.15;
                if (celsius is > 0 and < 150 && (hottest is null || celsius > hottest))
                {
                    hottest = Math.Round(celsius, 1);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        return hottest;
    }

    private void OpenSensors()
    {
        DisposeSensors();
        try
        {
            var drive = _diskPath.TrimEnd('\\');
            var instance = new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames()
                .FirstOrDefault(name => name.Split(' ').Contains(drive, StringComparer.OrdinalIgnoreCase)) ?? "_Total";
            _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instance, readOnly: true);
            _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instance, readOnly: true);
            _diskPrimed = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _diskRead = _diskWrite = null;
        }

        try
        {
            _thermalZones = [.. new PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames()
                .Select(name => new PerformanceCounter("Thermal Zone Information", "Temperature", name, readOnly: true))];
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _thermalZones = [];
        }
    }

    private NetworkSample SampleNetwork(TimeSpan now, TimeSpan elapsed)
    {
        var nic = SelectInterface();
        if (nic is null)
        {
            _previousNetwork = null;
            _previousInterface = string.Empty;
            return new NetworkSample(false, "Network", "Disconnected", string.Empty, string.Empty, null, null, null);
        }

        var wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        if (wifi && (nic.Id != _previousInterface || now - _wifiCheckedAt >= ConnectionRefresh))
        {
            _wifi = ReadWifi(nic.Name);
            _wifiCheckedAt = now;
        }

        (long Received, long Sent)? counters = null;
        try
        {
            var statistics = nic.GetIPStatistics();
            counters = (statistics.BytesReceived, statistics.BytesSent);
        }
        catch (NetworkInformationException)
        {
        }

        var sameInterface = nic.Id == _previousInterface;
        var download = sameInterface ? Rates.PerSecond(_previousNetwork?.Received, counters?.Received, elapsed) : null;
        var upload = sameInterface ? Rates.PerSecond(_previousNetwork?.Sent, counters?.Sent, elapsed) : null;
        _previousNetwork = counters;
        _previousInterface = nic.Id;

        var kind = nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
            _ => "Network",
        };
        var name = wifi && _wifi.Ssid.Length > 0 ? _wifi.Ssid : nic.Name;
        return new NetworkSample(true, kind, name, nic.Description, AddressOf(nic), wifi ? _wifi.Signal : null, download, upload);
    }

    /// <summary>Uses the kernel's best route to a public address (no packets are sent), like `ip route get`.</summary>
    private static NetworkInterface? SelectInterface()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return null;
        }

        var candidates = all.Where(n => n.OperationalStatus == OperationalStatus.Up
                                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();

        if (GetBestInterface(0x01010101, out var bestIndex) == 0)
        {
            var best = candidates.FirstOrDefault(n => IndexOf(n) == bestIndex);
            if (best is not null)
            {
                return best;
            }
        }

        return candidates.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)));
    }

    private static int? IndexOf(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static string AddressOf(NetworkInterface nic)
    {
        var addresses = nic.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToList();
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal);
        return address?.ToString() ?? string.Empty;
    }

    private static (string Ssid, int? Signal) ReadWifi(string interfaceName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return (string.Empty, null);
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(2000) || !output.Wait(500))
            {
                process.Kill();
                return (string.Empty, null);
            }

            // netsh prints one block per wireless adapter, each starting with "Name".
            var blocks = BlockSplit().Split(output.Result);
            var block = blocks.FirstOrDefault(b => b.Contains(interfaceName, StringComparison.OrdinalIgnoreCase)) ?? output.Result;
            var ssid = SsidLine().Match(block);
            var signal = SignalLine().Match(block);
            return (ssid.Success ? ssid.Groups[1].Value.Trim() : string.Empty,
                    signal.Success ? int.Parse(signal.Groups[1].Value) : null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (string.Empty, null);
        }
    }

    [GeneratedRegex(@"(?m)^\s*Name\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex BlockSplit();

    [GeneratedRegex(@"(?m)^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SsidLine();

    [GeneratedRegex(@"(?m)^\s*Signal\s*:\s*(\d+)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex SignalLine();

    private void DisposeSensors()
    {
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
        foreach (var zone in _thermalZones)
        {
            zone.Dispose();
        }

        _thermalZones = [];
    }

    public void Dispose() => DisposeSensors();
}
