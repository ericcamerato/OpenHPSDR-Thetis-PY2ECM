// Offline-only smoke test: no radio or Thetis database is opened.
using System;
using System.Collections.Generic;
using System.Threading;

namespace Thetis
{
    internal static class cmaster
    {
        internal static void SetCwRxAudioRun(int active) { }
    }

    internal static class NeuralCwSmoke
    {
        private static int Main()
        {
            const int rate = 48000;
            const int dot = 2880; // 20 WPM
            var keyed = new List<bool>();
            string[] letters = { "-.-.", "--.-", "-.-.", "--.-", "-.-.", "--.-" };
            foreach (string letter in letters)
            {
                foreach (char mark in letter)
                {
                    for (int i = 0; i < (mark == '-' ? 3 : 1) * dot; i++) keyed.Add(true);
                    for (int i = 0; i < dot; i++) keyed.Add(false);
                }
                for (int i = 0; i < 3 * dot; i++) keyed.Add(false);
            }
            for (int i = 0; i < 3 * rate; i++) keyed.Add(false);
            string result = "";
            using (var decoder = new NeuralCwDecoder(rate))
            {
                decoder.TextReceived += text => { result += text; Console.Write(text); };
                decoder.StatusChanged += text => Console.Error.WriteLine(text);
                decoder.Start();
                double[] stereo = new double[960];
                for (int offset = 0; offset < keyed.Count; offset += 480)
                {
                    int count = Math.Min(480, keyed.Count - offset);
                    for (int i = 0; i < count; i++)
                    {
                        double sample = keyed[offset + i] ? 0.5 * Math.Sin(2 * Math.PI * 600 * (offset + i) / rate) : 0;
                        stereo[2 * i] = stereo[2 * i + 1] = sample;
                    }
                    unsafe { fixed (double* pointer = stereo) decoder.Enqueue(count, pointer); }
                    Thread.Sleep(10);
                }
                Thread.Sleep(2000);
            }
            Console.WriteLine();
            return result.Contains("C") || result.Contains("Q") ? 0 : 1;
        }
    }
}
