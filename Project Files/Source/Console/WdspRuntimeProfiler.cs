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
        // The normal run remains free of profiling work and CSVs; only an
        // explicit diagnostic launch enables collection.
        private static readonly bool PerformanceCsvEnabled =
            !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("THETIS_WDSP_PROFILE")) &&
            Environment.GetEnvironmentVariable("THETIS_WDSP_PROFILE") != "0";
        private const int CategoryCount = 9;
        private const int MaxChannels = 32;
        internal const int DisplayFrame = 0;
        internal const int DisplayPan = 1;
        internal const int DisplayWaterfall = 2;
        internal const int DisplayWaterfallPrepare = 3;
        internal const int DisplayWaterfallUpload = 4;
        internal const int DisplayPresent = 5;
        internal const int DisplayPanGrid = 6;
        internal const int DisplayPan3D = 7;
        internal const int DisplayPanTrace = 8;
        internal const int DisplayPanOverlays = 9;
        internal const int DisplayEndDraw = 10;
        internal const int DisplayPanGpuFill = 11;
        internal const int DisplayWaterfallGpuSelected = 12;
        internal const int DisplayWaterfallCpuSelected = 13;
        internal const int DisplayPanGpuSelected = 14;
        internal const int DisplayPanCpuSelected = 15;
        internal const int DisplayFrameGpuSelected = 16;
        internal const int DisplayFrameCpuSelected = 17;
        internal const int DisplayPanTraceBatchSelected = 18;
        internal const int DisplayPanTraceCurrentSelected = 19;
        internal const int DisplayPanTraceGpuSelected = 20;
        private static readonly string[] DisplayNames = {
            "display_frame", "display_pan", "display_waterfall",
            "display_waterfall_prepare", "display_waterfall_row_upload", "display_present",
            "display_pan_grid", "display_pan_3d", "display_pan_trace", "display_pan_overlays",
            "display_enddraw", "display_pan_gpu_fill",
            "display_waterfall_gpu_selected", "display_waterfall_cpu_selected",
            "display_pan_gpu_selected", "display_pan_cpu_selected",
            "display_frame_gpu_selected", "display_frame_cpu_selected",
            "display_pan_trace_batch_selected", "display_pan_trace_current_selected",
            "display_pan_trace_gpu_selected"
        };
        private static readonly long[] displayTicks = new long[DisplayNames.Length];
        private static readonly long[] displayCounts = new long[DisplayNames.Length];
        private static volatile bool displayEnabled;
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
            displayEnabled = true;
            writer = new Thread(WriteLoop);
            writer.IsBackground = true;
            writer.Priority = ThreadPriority.BelowNormal;
            writer.Name = "WDSP profile CSV";
            writer.Start();
        }

        internal static WdspRuntimeProfiler StartIfRequested()
        {
            if (!PerformanceCsvEnabled) return null;
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

        internal static bool DisplayEnabled { get { return displayEnabled; } }

        internal static void RecordDisplay(int category, long startTicks)
        {
            if (!displayEnabled || startTicks == 0) return;
            Interlocked.Add(ref displayTicks[category], Stopwatch.GetTimestamp() - startTicks);
            Interlocked.Increment(ref displayCounts[category]);
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
                        for (int category = 0; category < DisplayNames.Length; category++)
                        {
                            long count = Interlocked.Exchange(ref displayCounts[category], 0);
                            long elapsedTicks = Interlocked.Exchange(ref displayTicks[category], 0);
                            if (count == 0) continue;
                            double totalMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                            double averageUs = totalMs * 1000.0 / count;
                            csv.Append(utc).Append(",-1,")
                                .Append(DisplayNames[category]).Append(',').Append(count).Append(',')
                                .Append(averageUs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                                .Append(totalMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                                .Append(cpuPercent.ToString("F1", CultureInfo.InvariantCulture)).Append("\r\n");
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
            displayEnabled = false;
            SetWDSPProfileEnabled(0);
            stop.Set();
            if (writer.Join(1500)) stop.Dispose();
        }
    }
}
