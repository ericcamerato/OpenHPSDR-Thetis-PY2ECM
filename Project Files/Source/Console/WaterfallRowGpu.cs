// GPU colour mapping for a single 2D waterfall row. The existing Direct2D
// waterfall bitmap remains responsible for scrolling and frequency shifts.
// Copyright (C) 2026 Eric Camerato
// GPL-2.0-or-later; see the repository license.

using System;
using System.Runtime.InteropServices;
using System.Text;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Color = System.Drawing.Color;

namespace Thetis
{
    internal sealed class WaterfallRowGpu : IDisposable
    {
        private const string ShaderSource = @"
Texture2D<float> Samples : register(t0);
Texture2D<float4> CustomPalette : register(t1);
cbuffer Frame : register(b0)
{
    float4 dimensions; // output width, sample count, decimation, custom palette flag
    float4 thresholds; // low, high, unused, unused
    float4 lowColour;  // RGB values in the same 0..255 range as the CPU palette
};

float4 fullScreen(uint id : SV_VertexID) : SV_POSITION
{
    float2 position = id == 0 ? float2(-1.0, -1.0) :
                      id == 1 ? float2(-1.0, 3.0) : float2(3.0, -1.0);
    return float4(position, 0.0, 1.0);
}

float3 enhanced(float value)
{
    float p = (value - thresholds.x) / max(thresholds.y - thresholds.x, 0.0001);
    if (p < 2.0 / 9.0)
    {
        float t = p / (2.0 / 9.0);
        return float3((1.0 - t) * lowColour.r,
                      (1.0 - t) * lowColour.g,
                      lowColour.b + t * (255.0 - lowColour.b));
    }
    if (p < 3.0 / 9.0)
        return float3(0.0, (p - 2.0 / 9.0) * 9.0 * 255.0, 255.0);
    if (p < 4.0 / 9.0)
        return float3(0.0, 255.0, (1.0 - (p - 3.0 / 9.0) * 9.0) * 255.0);
    if (p < 5.0 / 9.0)
        return float3((p - 4.0 / 9.0) * 9.0 * 255.0, 255.0, 0.0);
    if (p < 7.0 / 9.0)
        return float3(255.0, (1.0 - (p - 5.0 / 9.0) * 4.5) * 255.0, 0.0);
    if (p < 8.0 / 9.0)
        return float3(255.0, 0.0, (p - 7.0 / 9.0) * 9.0 * 255.0);
    float t = (p - 8.0 / 9.0) * 9.0;
    return float3((1.0 - 0.25 * t) * 255.0, t * 127.5, 255.0);
}

float4 colourRow(float4 position : SV_POSITION) : SV_TARGET
{
    uint x = (uint)position.x;
    uint column = x / max((uint)dimensions.z, 1u);
    if (x >= (uint)dimensions.x || column >= (uint)dimensions.y)
        return float4(0.0, 0.0, 0.0, 0.0);
    float value = Samples.Load(int3(column, 0, 0));
    float3 rgb;
    if (dimensions.w > 0.5)
    {
        float p = saturate((value - thresholds.x) /
            max(thresholds.y - thresholds.x, 0.0001));
        uint index = value <= thresholds.x ? 0u :
                     value >= thresholds.y ? 100u : (uint)(p * 100.0);
        rgb = CustomPalette.Load(int3(index, 0, 0)).rgb;
        return float4(rgb, 1.0);
    }
    if (value <= thresholds.x) rgb = lowColour.rgb;
    else if (value >= thresholds.y) rgb = float3(192.0, 124.0, 255.0);
    else rgb = enhanced(value);
    // The CPU path truncates each channel to byte before storing BGRA.
    return float4(floor(clamp(rgb, 0.0, 255.0)) / 255.0, 1.0);
}";

        [StructLayout(LayoutKind.Sequential)]
        private struct FrameConstants
        {
            public Vector4 Dimensions;
            public Vector4 Thresholds;
            public Vector4 LowColour;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr BlobGetBufferPointer(IntPtr blob);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate UIntPtr BlobGetBufferSize(IntPtr blob);

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Ansi)]
        private static extern int D3DCompile(
            [MarshalAs(UnmanagedType.LPStr)] string source, UIntPtr sourceLength,
            [MarshalAs(UnmanagedType.LPStr)] string sourceName,
            IntPtr defines, IntPtr include,
            [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
            [MarshalAs(UnmanagedType.LPStr)] string target,
            uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

        private readonly Device device;
        private readonly DeviceContext context;
        private readonly VertexShader vertexShader;
        private readonly PixelShader pixelShader;
        private readonly Buffer frameBuffer;
        private readonly RasterizerState rasterizerState;
        private Texture2D sampleTexture;
        private ShaderResourceView sampleView;
        private Texture2D paletteTexture;
        private ShaderResourceView paletteView;
        private Texture2D outputTexture;
        private RenderTargetView outputView;
        private SharpDX.Direct2D1.Bitmap outputBitmap;
        private int sampleCount;
        private int outputWidth;
        private readonly byte[] paletteBytes = new byte[101 * 4];

        internal SharpDX.Direct2D1.Bitmap Bitmap { get { return outputBitmap; } }

        internal WaterfallRowGpu(Device d3dDevice)
        {
            if (d3dDevice.FeatureLevel < FeatureLevel.Level_10_0)
                throw new NotSupportedException("Waterfall shader requires Direct3D feature level 10.0.");
            device = d3dDevice;
            context = device.ImmediateContext;
            vertexShader = new VertexShader(device, Compile("fullScreen", "vs_4_0"));
            pixelShader = new PixelShader(device, Compile("colourRow", "ps_4_0"));
            frameBuffer = new Buffer(device, Utilities.SizeOf<FrameConstants>(),
                ResourceUsage.Default, BindFlags.ConstantBuffer,
                CpuAccessFlags.None, ResourceOptionFlags.None, 0);
            rasterizerState = new RasterizerState(device, new RasterizerStateDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                IsDepthClipEnabled = false,
                IsScissorEnabled = false,
                IsMultisampleEnabled = false
            });
            paletteTexture = new Texture2D(device, new Texture2DDescription
            {
                Width = 101, Height = 1, ArraySize = 1, MipLevels = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.Write, OptionFlags = ResourceOptionFlags.None
            });
            paletteView = new ShaderResourceView(device, paletteTexture);
        }

        private static byte[] Compile(string entryPoint, string profile)
        {
            IntPtr code;
            IntPtr errors;
            int length = Encoding.ASCII.GetByteCount(ShaderSource);
            int result = D3DCompile(ShaderSource, new UIntPtr((uint)length),
                "WaterfallRowGpu", IntPtr.Zero, IntPtr.Zero,
                entryPoint, profile, 0, 0, out code, out errors);
            try
            {
                if (result < 0 || code == IntPtr.Zero)
                    throw new InvalidOperationException("Waterfall shader compilation failed: " +
                        (errors == IntPtr.Zero ? result.ToString("X8") : ReadBlobText(errors)));
                return ReadBlob(code);
            }
            finally
            {
                if (code != IntPtr.Zero) Marshal.Release(code);
                if (errors != IntPtr.Zero) Marshal.Release(errors);
            }
        }

        private static byte[] ReadBlob(IntPtr blob)
        {
            IntPtr methods = Marshal.ReadIntPtr(blob);
            var getPointer = (BlobGetBufferPointer)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(methods, 3 * IntPtr.Size), typeof(BlobGetBufferPointer));
            var getSize = (BlobGetBufferSize)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(methods, 4 * IntPtr.Size), typeof(BlobGetBufferSize));
            int size = checked((int)getSize(blob).ToUInt64());
            byte[] bytes = new byte[size];
            Marshal.Copy(getPointer(blob), bytes, 0, size);
            return bytes;
        }

        private static string ReadBlobText(IntPtr blob)
        {
            return Encoding.ASCII.GetString(ReadBlob(blob));
        }

        private void EnsureSamples(int columns)
        {
            if (sampleCount == columns) return;
            if (sampleView != null) sampleView.Dispose();
            if (sampleTexture != null) sampleTexture.Dispose();
            sampleCount = columns;
            sampleTexture = new Texture2D(device, new Texture2DDescription
            {
                Width = columns, Height = 1, ArraySize = 1, MipLevels = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.Write, OptionFlags = ResourceOptionFlags.None
            });
            sampleView = new ShaderResourceView(device, sampleTexture);
        }

        private void EnsureOutput(SharpDX.Direct2D1.RenderTarget target, int width)
        {
            if (outputBitmap != null && outputWidth == width) return;
            if (outputBitmap != null) outputBitmap.Dispose();
            if (outputView != null) outputView.Dispose();
            if (outputTexture != null) outputTexture.Dispose();
            outputWidth = width;
            outputTexture = new Texture2D(device, new Texture2DDescription
            {
                Width = width, Height = 1, ArraySize = 1, MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None, OptionFlags = ResourceOptionFlags.None
            });
            outputView = new RenderTargetView(device, outputTexture);
            using (var surface = outputTexture.QueryInterface<Surface>())
            {
                var properties = new SharpDX.Direct2D1.BitmapProperties(
                    new SharpDX.Direct2D1.PixelFormat(Format.B8G8R8A8_UNorm,
                        SharpDX.Direct2D1.AlphaMode.Premultiplied));
                outputBitmap = new SharpDX.Direct2D1.Bitmap(target, surface, properties);
            }
        }

        internal void Render(SharpDX.Direct2D1.RenderTarget target, int width,
            float[] samples, int columns, int decimation, float low, float high,
            Color lowColor, Color[] customPalette)
        {
            EnsureSamples(columns);
            EnsureOutput(target, width);
            DataBox box = context.MapSubresource(sampleTexture, 0,
                MapMode.WriteDiscard, MapFlags.None);
            try { Marshal.Copy(samples, 0, box.DataPointer, columns); }
            finally { context.UnmapSubresource(sampleTexture, 0); }

            if (customPalette != null)
            {
                for (int i = 0; i < 101; i++)
                {
                    paletteBytes[i * 4 + 0] = customPalette[i].R;
                    paletteBytes[i * 4 + 1] = customPalette[i].G;
                    paletteBytes[i * 4 + 2] = customPalette[i].B;
                    paletteBytes[i * 4 + 3] = 255;
                }
                box = context.MapSubresource(paletteTexture, 0,
                    MapMode.WriteDiscard, MapFlags.None);
                try { Marshal.Copy(paletteBytes, 0, box.DataPointer, paletteBytes.Length); }
                finally { context.UnmapSubresource(paletteTexture, 0); }
            }

            FrameConstants frame = new FrameConstants
            {
                Dimensions = new Vector4(width, columns, decimation,
                    customPalette == null ? 0f : 1f),
                Thresholds = new Vector4(low, high, 0f, 0f),
                LowColour = new Vector4(lowColor.R, lowColor.G, lowColor.B, 0f)
            };
            context.OutputMerger.SetRenderTargets(outputView);
            context.OutputMerger.SetBlendState((BlendState)null);
            context.Rasterizer.State = rasterizerState;
            context.Rasterizer.SetViewport(0, 0, width, 1);
            context.ClearRenderTargetView(outputView, new Color4(0f, 0f, 0f, 0f));
            context.InputAssembler.InputLayout = null;
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.UpdateSubresource(ref frame, frameBuffer);
            context.VertexShader.Set(vertexShader);
            context.PixelShader.Set(pixelShader);
            context.PixelShader.SetConstantBuffer(0, frameBuffer);
            context.PixelShader.SetShaderResource(0, sampleView);
            context.PixelShader.SetShaderResource(1, paletteView);
            context.Draw(3, 0);
            context.PixelShader.SetShaderResource(0, null);
            context.PixelShader.SetShaderResource(1, null);
            context.OutputMerger.SetRenderTargets((RenderTargetView)null);
            context.Flush();
        }

        public void Dispose()
        {
            if (outputBitmap != null) outputBitmap.Dispose();
            if (outputView != null) outputView.Dispose();
            if (outputTexture != null) outputTexture.Dispose();
            if (sampleView != null) sampleView.Dispose();
            if (sampleTexture != null) sampleTexture.Dispose();
            paletteView.Dispose();
            paletteTexture.Dispose();
            rasterizerState.Dispose();
            frameBuffer.Dispose();
            pixelShader.Dispose();
            vertexShader.Dispose();
        }
    }
}
