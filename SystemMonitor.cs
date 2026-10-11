using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterWebLauncher
{
    /// <summary>一次资源采样结果。</summary>
    public sealed class SystemUsage
    {
        /// <summary>整机 CPU 占用率（0-100，按全部逻辑核心计算）。</summary>
        public double CpuPercent;

        /// <summary>整机内存占用率（0-100）。</summary>
        public double MemPercent;

        public double TotalMemGB;
        public double UsedMemGB;

        /// <summary>本程序 CPU 占用率（0-100，100% = 占满所有核心）。</summary>
        public double AppCpuPercent;

        /// <summary>本程序内存占用（工作集，MB）。</summary>
        public double AppMemMB;

        public int AppThreads;

        /// <summary>本程序内存占整机物理内存的比例（0-100）。</summary>
        public double AppMemPercent;
    }

    /// <summary>
    /// 系统资源采样器：用 GetSystemTimes / GlobalMemoryStatusEx 计算 CPU 与内存占用，
    /// 不依赖性能计数器（PerformanceCounter），避免额外依赖。
    /// 第一次调用只做基准，返回的 CPU 值为 0；之后每次调用给出区间平均值。
    /// </summary>
    public sealed class UsageSampler
    {
        private readonly Process _self = Process.GetCurrentProcess();

        private ulong _prevIdle, _prevKernel, _prevUser;
        private TimeSpan _prevAppCpu;
        private DateTime _prevTime = DateTime.MinValue;
        private bool _primed;

        public SystemUsage Sample()
        {
            var usage = new SystemUsage();

            // ---------- 整机内存 ----------
            try
            {
                var mem = new MemoryStatusEx();
                if (GlobalMemoryStatusEx(mem))
                {
                    usage.MemPercent = mem.MemoryLoad;
                    usage.TotalMemGB = mem.TotalPhys / 1024.0 / 1024.0 / 1024.0;
                    double used = (mem.TotalPhys - mem.AvailPhys) / 1024.0 / 1024.0 / 1024.0;
                    usage.UsedMemGB = used;
                }
            }
            catch { }

            // ---------- 整机 CPU ----------
            try
            {
                if (GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    ulong i = ToUlong(idle), k = ToUlong(kernel), u = ToUlong(user);

                    if (_primed)
                    {
                        ulong dIdle = i - _prevIdle;
                        ulong dKernel = k - _prevKernel;
                        ulong dUser = u - _prevUser;
                        ulong total = dKernel + dUser;      // kernel 已包含 idle
                        if (total > 0)
                        {
                            double busy = total - Math.Min(dIdle, total);
                            usage.CpuPercent = Math.Round(busy * 100.0 / total, 1);
                        }
                    }

                    _prevIdle = i;
                    _prevKernel = k;
                    _prevUser = u;
                }
            }
            catch { }

            // ---------- 本程序占用 ----------
            try
            {
                _self.Refresh();
                var now = DateTime.UtcNow;
                var cpu = _self.TotalProcessorTime;

                if (_primed && _prevTime != DateTime.MinValue)
                {
                    double elapsedMs = (now - _prevTime).TotalMilliseconds;
                    double cpuMs = (cpu - _prevAppCpu).TotalMilliseconds;
                    double cores = Math.Max(1, Environment.ProcessorCount);
                    if (elapsedMs > 0)
                    {
                        usage.AppCpuPercent = Math.Round(Math.Max(0, cpuMs) / (elapsedMs * cores) * 100.0, 1);
                    }
                }

                _prevAppCpu = cpu;
                _prevTime = now;

                usage.AppMemMB = Math.Round(_self.WorkingSet64 / 1024.0 / 1024.0, 1);
                usage.AppThreads = _self.Threads.Count;
                if (usage.TotalMemGB > 0)
                    usage.AppMemPercent = Math.Round(usage.AppMemMB / 1024.0 / usage.TotalMemGB * 100.0, 2);
            }
            catch { }

            _primed = true;
            return usage;
        }

        private static ulong ToUlong(FileTime ft) => ((ulong)ft.High << 32) | ft.Low;

        // ---------------- Win32 ----------------
        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

        [StructLayout(LayoutKind.Sequential)]
        private sealed class MemoryStatusEx
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
    }
}
