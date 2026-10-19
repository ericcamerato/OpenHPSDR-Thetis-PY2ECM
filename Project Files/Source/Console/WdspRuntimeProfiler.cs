using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Thetis
{
    // Opt-in diagnostics. Collection is native; CSV I/O never runs on an audio thread.
    internal sealed class WdspRuntimeProfiler : IDisposable
    {
        private const int CategoryCount = 9;
        private const int MaxChannels = 32;
        private static readonly string[] Names = {
            "rx_loop", "rx_frontend", "rx_demod", "rx_noise",
            "rx_filter_agc", "rx_post", "spectrum_total",
            "spectrum_fft", "spectrum_post"
        };

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetWDSPProfileEnabled(int enabled);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetWDSPProfileSnapshot(int channel,
            [Out] long[] ticks, [Out] long[] counts, int capacity);

        private readonly string path;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly Thread writer;
        private bool disposed;

        private WdspRuntimeProfiler(string path)
        {
            this.path = path;
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                File.AppendAllText(path,
                    "utc,channel,category,calls,average_us,total_ms,process_cpu_percent_of_one_core\r\n");
            SetWDSPProfileEnabled(1);
            writer = new Thread(WriteLoop);
            writer.IsBackground = true;
            writer.Priority = ThreadPriority.BelowNormal;
            writer.Name = "WDSP profile CSV";
            writer.Start();
        }

        internal static WdspRuntimeProfiler StartIfRequested()
        {
            string setting = Environment.GetEnvironmentVariable("THETIS_WDSP_PROFILE");
            if (String.IsNullOrWhiteSpace(setting) || setting == "0") return null;
            try
            {
                string path = setting == "1"
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "wdsp-profile-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv")
                    : Path.GetFullPath(setting);
                return new WdspRuntimeProfiler(path);
            }
            catch (Exception error)
            {
                Debug.WriteLine("WDSP profiler unavailable: " + error.Message);
                return null;
            }
        }

        private void WriteLoop()
        {
            long[] ticks = new long[CategoryCount];
            long[] counts = new long[CategoryCount];
            using (Process process = Process.GetCurrentProcess())
            {
                TimeSpan previousCpu = process.TotalProcessorTime;
                long previousTime = Stopwatch.GetTimestamp();
                while (true)
                {
                    bool ending = stop.WaitOne(5000);
                    try
                    {
                        long now = Stopwatch.GetTimestamp();
                        process.Refresh();
                        TimeSpan cpu = process.TotalProcessorTime;
                        double elapsedSeconds = (now - previousTime) / (double)Stopwatch.Frequency;
                        double cpuPercent = elapsedSeconds > 0
                            ? (cpu - previousCpu).TotalSeconds / elapsedSeconds * 100.0 : 0.0;
                        previousTime = now;
                        previousCpu = cpu;
                        string utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                        StringBuilder csv = new StringBuilder();
                        for (int channel = 0; channel < MaxChannels; channel++)
                        {
                            if (GetWDSPProfileSnapshot(channel, ticks, counts, CategoryCount) != CategoryCount)
                                continue;
                            for (int category = 0; category < CategoryCount; category++)
                            {
                                if (counts[category] == 0) continue;
                                double totalMs = ticks[category] * 1000.0 / Stopwatch.Frequency;
                                double averageUs = totalMs * 1000.0 / counts[category];
                                csv.Append(utc).Append(',').Append(channel).Append(',')
                                    .Append(Names[category]).Append(',').Append(counts[category]).Append(',')
                                    .Append(averageUs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                                    .Append(totalMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                                    .Append(cpuPercent.ToString("F1", CultureInfo.InvariantCulture)).Append("\r\n");
                            }
                        }
                        if (csv.Length > 0) File.AppendAllText(path, csv.ToString());
                    }
                    catch (Exception error)
                    {
                        Debug.WriteLine("WDSP profiler write failed: " + error.Message);
                    }
                    if (ending) break;
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            SetWDSPProfileEnabled(0);
            stop.Set();
            if (writer.Join(1500)) stop.Dispose();
        }
    }
}
