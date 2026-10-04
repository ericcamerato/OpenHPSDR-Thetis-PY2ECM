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
        private static int Main(string[] args)
        {
            bool rightOnly = args.Length > 0 && args[0] == "--right-only";
            bool weak = Array.IndexOf(args, "--weak") >= 0;
            bool silence = Array.IndexOf(args, "--silence") >= 0;
            bool marginal = Array.IndexOf(args, "--marginal") >= 0;
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
                var noise = new Random(42);
                for (int offset = 0; offset < keyed.Count; offset += 480)
                {
                    int count = Math.Min(480, keyed.Count - offset);
                    for (int i = 0; i < count; i++)
                    {
                        double sample = !silence && keyed[offset + i] ? (weak || marginal ? 0.01 : 0.5) * Math.Sin(2 * Math.PI * 600 * (offset + i) / rate) : 0;
                        if (weak || silence || marginal) sample += (noise.NextDouble() * 2 - 1) * (marginal ? 0.005 : 0.001);
                        stereo[2 * i] = rightOnly ? 0 : sample;
                        stereo[2 * i + 1] = sample;
                    }
                    unsafe { fixed (double* pointer = stereo) decoder.Enqueue(count, pointer); }
                    Thread.Sleep(10);
                }
                Thread.Sleep(2000);
                Console.Error.WriteLine("Last CW tone {0:F0} Hz, key ratio {1:F1}", decoder.ToneHz, decoder.KeyingRatio);
            }
            Console.WriteLine();
            return silence ? (result.Trim().Length == 0 ? 0 : 1) : (result.Contains("C") || result.Contains("Q") ? 0 : 1);
        }
    }
}
