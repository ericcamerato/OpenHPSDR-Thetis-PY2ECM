// RX-only DeepFist integration for CWX. The model and preprocessing contract
// are credited in THIRD-PARTY-NEURAL-CW.txt. No Lyra application code is used.
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json.Linq;

namespace Thetis
{
    internal sealed class NeuralCwDecoder : IDisposable
    {
        private const int Rate = 3200;
        private const int Window = 6 * Rate;
        private const int FftSize = 256;
        private const int Hop = 48;
        private const int Bins = 65;
        private const int FirSize = 121;
        private static readonly string ModelDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
        private readonly ConcurrentQueue<AudioBlock> pending = new ConcurrentQueue<AudioBlock>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly float[] ring = new float[Window];
        private readonly double[] fir = new double[FirSize];
        private readonly double[] history = new double[FirSize];
        private readonly float[] hann = new float[FftSize];
        private readonly InferenceSession session;
        private readonly string[] tokens;
        private readonly Thread worker;
        private readonly int decimation;
        private volatile bool running;
        private int queued;
        private int histPos;
        private int phase;
        private long total;
        private double committed;
        private bool idle;
        private bool disposed;
        private volatile string fault;

        private struct AudioBlock
        {
            public float[] Samples;
            public int Length;
        }

        internal static NeuralCwDecoder Current { get; private set; }
        internal string Error { get { return fault; } }
        internal event Action<string> TextReceived;
        internal event Action<string> StatusChanged;

        internal NeuralCwDecoder(int inputRate)
        {
            if (inputRate <= 0 || inputRate % Rate != 0)
                throw new NotSupportedException("CW neural RX requires an audio rate divisible by 3200 Hz.");
            decimation = inputRate / Rate;
            string model = Path.Combine(ModelDir, "deepfist.onnx");
            string sidecar = model + ".json";
            if (!File.Exists(model) || !File.Exists(sidecar))
                throw new FileNotFoundException("DeepFist model or token sidecar is missing in the models folder.");
            tokens = JObject.Parse(File.ReadAllText(sidecar))["tokens"].Values<string>().ToArray();
            if (tokens.Length != 48) throw new InvalidDataException("Unexpected DeepFist token table.");
            var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
            session = new InferenceSession(model, options);
            options.Dispose();
            BuildFir(inputRate);
            for (int n = 0; n < FftSize; n++)
                hann[n] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / FftSize));
            running = true;
            worker = new Thread(Work) { IsBackground = true, Name = "CW neural RX", Priority = ThreadPriority.BelowNormal };
        }

        internal void Start()
        {
            Current = this;
            worker.Start();
            cmaster.SetCwRxAudioRun(1);
        }

        // Called on the ChannelMaster real-time audio thread. Never infer or wait here.
        internal unsafe void Enqueue(int count, double* stereo)
        {
            if (!running || stereo == null || count <= 0 || count > 65536 || Volatile.Read(ref queued) >= 8) return;
            float[] samples = ArrayPool<float>.Shared.Rent(count);
            for (int i = 0; i < count; i++) samples[i] = (float)stereo[2 * i];
            Interlocked.Increment(ref queued);
            pending.Enqueue(new AudioBlock { Samples = samples, Length = count });
            wake.Set();
        }

        private void BuildFir(int inputRate)
        {
            double fc = 1440.0 / inputRate;
            double sum = 0;
            for (int i = 0; i < FirSize; i++)
            {
                double x = 2 * fc * (i - (FirSize - 1) / 2.0);
                double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double hamming = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (FirSize - 1));
                fir[i] = 2 * fc * sinc * hamming;
                sum += fir[i];
            }
            for (int i = 0; i < FirSize; i++) fir[i] /= sum;
        }

        private void Work()
        {
            long lastInference = 0;
            try
            {
                while (running)
                {
                    wake.WaitOne(50);
                    AudioBlock block;
                    while (pending.TryDequeue(out block))
                    {
                        Interlocked.Decrement(ref queued);
                        try { Resample(block.Samples, block.Length); }
                        finally { ArrayPool<float>.Shared.Return(block.Samples); }
                    }
                    if (total < Rate || total - lastInference < Rate * 4 / 10) continue;
                    lastInference = total;
                    Decode();
                }
            }
            catch (Exception e)
            {
                fault = e.Message;
                running = false;
                StatusChanged?.Invoke("CW RX error: " + e.Message);
            }
        }

        private void Resample(float[] input, int count)
        {
            for (int i = 0; i < count; i++)
            {
                history[histPos] = input[i];
                histPos = (histPos + 1) % FirSize;
                if (++phase != decimation) continue;
                phase = 0;
                double value = 0;
                int k = (histPos - 1 + FirSize) % FirSize;
                for (int j = 0; j < FirSize; j++)
                {
                    value += fir[j] * history[k];
                    k = (k - 1 + FirSize) % FirSize;
                }
                ring[total % Window] = (float)value;
                total++;
            }
        }

        private void Decode()
        {
            float[] audio = new float[Window];
            int start = (int)(total % Window);
            Array.Copy(ring, start, audio, 0, Window - start);
            Array.Copy(ring, 0, audio, Window - start, start);
            double endTime = (double)total / Rate;
            double settleTo = endTime - 1.3;
            double tone = DetectTone(audio);
            float[] conditioned = Condition(audio, tone, out double ratio);
            if (ratio < 12.0)
            {
                committed = Math.Max(committed, settleTo);
                if (!idle) { idle = true; TextReceived?.Invoke(" "); }
                return;
            }
            idle = false;
            int frames = 1 + Window / Hop;
            float[] spectrogram = Spectrogram(conditioned, frames);
            var tensor = new DenseTensor<float>(spectrogram, new[] { 1, 1, Bins, frames });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor("spectrogram", tensor) };
            using (var results = session.Run(inputs))
            {
                var output = results.First().AsTensor<float>();
                int tCount = output.Dimensions[0];
                int classes = output.Dimensions[2];
                if (classes != tokens.Length) return;
                int previous = -1;
                string emitted = "";
                for (int t = 0; t < tCount; t++)
                {
                    int best = 0;
                    float score = output[t, 0, 0];
                    for (int c = 1; c < classes; c++)
                        if (output[t, 0, c] > score) { score = output[t, 0, c]; best = c; }
                    if (best != previous && best != 0)
                    {
                        double when = endTime - 6.0 + (double)t / tCount * 6.0;
                        if (when > committed && when <= settleTo)
                            emitted += tokens[best];
                    }
                    previous = best;
                }
                committed = Math.Max(committed, settleTo);
                if (emitted.Length > 0) TextReceived?.Invoke(emitted.Replace("=", "<BT>").Replace("+", "<AR>"));
            }
        }

        private static void Fft(Complex[] data)
        {
            int n = data.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { Complex tmp = data[i]; data[i] = data[j]; data[j] = tmp; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                Complex step = Complex.FromPolarCoordinates(1, -2.0 * Math.PI / len);
                for (int baseIndex = 0; baseIndex < n; baseIndex += len)
                {
                    Complex w = Complex.One;
                    for (int k = 0; k < len / 2; k++)
                    {
                        Complex u = data[baseIndex + k];
                        Complex v = data[baseIndex + k + len / 2] * w;
                        data[baseIndex + k] = u + v;
                        data[baseIndex + k + len / 2] = u - v;
                        w *= step;
                    }
                }
            }
        }

        private static double DetectTone(float[] audio)
        {
            var fft = new Complex[4096];
            for (int i = 0; i < fft.Length; i++) fft[i] = audio[audio.Length - fft.Length + i];
            Fft(fft);
            int best = 512;
            double peak = -1;
            for (int b = 512; b < 1536; b++)
            {
                double magnitude = fft[b].Magnitude;
                if (magnitude > peak) { peak = magnitude; best = b; }
            }
            return (double)best * Rate / 4096;
        }

        private static float[] Condition(float[] audio, double tone, out double ratio)
        {
            int n = audio.Length;
            float[] result = new float[n];
            float[] envelope = new float[n];
            double rms = Math.Sqrt(audio.Sum(x => (double)x * x) / n) + 1e-9;
            double alpha = 1 - Math.Exp(-2 * Math.PI * 45 / Rate);
            double down = -2 * Math.PI * tone / Rate;
            double up = 2 * Math.PI * 600 / Rate;
            double bi = 0, bq = 0, ci = 0, cq = 0, peak = 0;
            for (int i = 0; i < n; i++)
            {
                double di = audio[i] / rms * Math.Cos(down * i);
                double dq = audio[i] / rms * Math.Sin(down * i);
                bi += alpha * (di - bi); bq += alpha * (dq - bq);
                ci += alpha * (bi - ci); cq += alpha * (bq - cq);
                envelope[i] = (float)Math.Sqrt(ci * ci + cq * cq);
                double v = ci * Math.Cos(up * i) - cq * Math.Sin(up * i);
                result[i] = (float)v;
                peak = Math.Max(peak, Math.Abs(v));
            }
            Array.Sort(envelope);
            ratio = (envelope[n - 1 - n / 10] * rms) / (envelope[n / 10] * rms + 1e-3);
            for (int i = 0; i < n; i++) result[i] /= (float)(peak + 1e-9);
            return result;
        }

        private float[] Spectrogram(float[] audio, int frames)
        {
            float[] tile = new float[Bins * frames];
            double mean = 0, m2 = 0;
            int count = 0;
            for (int t = 0; t < frames; t++)
            {
                var fft = new Complex[FftSize];
                for (int k = 0; k < FftSize; k++)
                {
                    int i = t * Hop + k - FftSize / 2;
                    while (i < 0 || i >= audio.Length)
                        i = i < 0 ? -i : 2 * audio.Length - 2 - i;
                    fft[k] = audio[i] * hann[k];
                }
                Fft(fft);
                for (int f = 0; f < Bins; f++)
                {
                    float value = (float)Math.Log(1 + fft[32 + f].Magnitude);
                    tile[f * frames + t] = value;
                    count++;
                    double delta = value - mean;
                    mean += delta / count;
                    m2 += delta * (value - mean);
                }
            }
            double scale = 1.0 / (Math.Sqrt(m2 / (count - 1)) + 1e-6);
            for (int i = 0; i < tile.Length; i++) tile[i] = (float)((tile[i] - mean) * scale);
            return tile;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cmaster.SetCwRxAudioRun(0);
            Current = null;
            running = false;
            wake.Set();
            worker.Join();
            AudioBlock block;
            while (pending.TryDequeue(out block)) ArrayPool<float>.Shared.Return(block.Samples);
            wake.Dispose();
            session.Dispose();
        }
    }
}
