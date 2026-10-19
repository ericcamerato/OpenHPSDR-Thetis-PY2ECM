//=================================================================
// ThreeDSpectrumGpu.cs
// GPU height-map renderer for the optional 3D spectrum display.
// Copyright (C) 2026 Eric Camerato
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.
//=================================================================

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Color = System.Drawing.Color;
using Device = SharpDX.Direct3D11.Device;
using Buffer = SharpDX.Direct3D11.Buffer;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace Thetis
{
    internal sealed class ThreeDSpectrumGpu : IDisposable
    {
        private const int PaletteSize = 256;
        private const string ShaderSource = @"
Texture2D<float> History : register(t0);
Texture2D<float4> Palette : register(t1);

cbuffer Frame : register(b0)
{
    float4 plot;      // width, height, history head, history row count
    float4 levels;    // floor dBm, height range dB, grid min dBm, grid span dB
    float4 shape;     // back width, depth span, max ridge, height curve
    float4 row;       // row age, columns, fill opacity, outline opacity
    float4 outline;   // configured data-line colour
};

struct Varying
{
    float4 position : SV_POSITION;
    float lut : TEXCOORD0;
    float depth : TEXCOORD1;
    float edge : TEXCOORD2;
};

Varying vertexAt(uint column, float edge)
{
    Varying o;
    int ring = ((int)plot.z - (int)row.x + (int)plot.w) % (int)plot.w;
    float dbm = History.Load(int3((int)column, ring, 0));
    float frequency = column / max(row.y - 1.0, 1.0);
    float depth = row.x / max(plot.w - 1.0, 1.0);
    float width = lerp(1.0, shape.x, depth);
    float x = 0.5 + (frequency - 0.5) * width;
    float baseline = 1.0 - depth * shape.y;
    float strength = saturate((dbm - levels.x) / max(levels.y, 1.0));
    float height = pow(strength, max(shape.w, 0.05)) * shape.z * width;
    float y = edge > 0.5 ? 1.0 : max(0.02, baseline - height);
    o.position = float4(x * 2.0 - 1.0, 1.0 - y * 2.0, 0.0, 1.0);
    float colour = saturate((dbm - levels.z) / max(levels.w, 1.0));
    // The lower curtain edge uses the panadapter's actual floor colour.
    o.lut = edge > 0.5 ? saturate((levels.x - levels.z) / max(levels.w, 1.0)) : colour;
    o.depth = depth;
    o.edge = edge;
    return o;
}

Varying fillVertex(uint id : SV_VertexID)
{
    return vertexAt(id / 2, (float)(id % 2));
}

Varying ridgeVertex(uint id : SV_VertexID)
{
    return vertexAt(id, 0.0);
}

Varying frontRibbonVertex(uint id : SV_VertexID)
{
    Varying o = vertexAt(id / 2, 0.0);
    float halfWidthPixels = 1.3;
    o.position.y += (id % 2 == 0 ? 1.0 : -1.0) *
        (2.0 * halfWidthPixels / max(plot.y, 1.0));
    return o;
}

float4 fillPixel(Varying v) : SV_TARGET
{
    int index = (int)round(saturate(v.lut) * 255.0);
    float3 rgb = Palette.Load(int3(index, 0, 0)).rgb;
    rgb *= lerp(1.0, 0.88, v.depth);
    float alpha = row.z * lerp(1.0, 0.70, v.depth);
    return float4(rgb * alpha, alpha);
}

float4 ridgePixel(Varying v) : SV_TARGET
{
    float alpha = row.w * lerp(1.0, 0.24, v.depth);
    int index = (int)round(saturate(v.lut) * 255.0);
    float3 lineColour = outline.w > 0.5 ? Palette.Load(int3(index, 0, 0)).rgb : outline.rgb;
    return float4(lineColour * alpha, alpha);
}";

        [StructLayout(LayoutKind.Sequential)]
        private struct FrameConstants
        {
            public Vector4 Plot;
            public Vector4 Levels;
            public Vector4 Shape;
            public Vector4 Row;
            public Vector4 Outline;
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
        private readonly int columns;
        private readonly int rows;
        private readonly VertexShader fillVS;
        private readonly VertexShader ridgeVS;
        private readonly VertexShader frontRibbonVS;
        private readonly PixelShader fillPS;
        private readonly PixelShader ridgePS;
        private readonly Buffer frameBuffer;
        private readonly BlendState blendState;
        private readonly RasterizerState rasterizerState;
        private readonly Texture2D historyTexture;
        private readonly ShaderResourceView historyView;
        private readonly Texture2D paletteTexture;
        private readonly ShaderResourceView paletteView;
        private Texture2D outputTexture;
        private RenderTargetView outputView;
        private SharpDX.Direct2D1.Bitmap outputBitmap;
        private int outputWidth;
        private int outputHeight;
        private int paletteHash;

        public SharpDX.Direct2D1.Bitmap Bitmap { get { return outputBitmap; } }

        public ThreeDSpectrumGpu(Device d3dDevice, int sampleColumns, int historyRows)
        {
            if (d3dDevice.FeatureLevel < FeatureLevel.Level_10_0)
                throw new NotSupportedException("3D mesh requires Direct3D feature level 10.0.");
            device = d3dDevice;
            context = device.ImmediateContext;
            columns = sampleColumns;
            rows = historyRows;

            fillVS = new VertexShader(device, Compile("fillVertex", "vs_4_0"));
            ridgeVS = new VertexShader(device, Compile("ridgeVertex", "vs_4_0"));
            frontRibbonVS = new VertexShader(device, Compile("frontRibbonVertex", "vs_4_0"));
            fillPS = new PixelShader(device, Compile("fillPixel", "ps_4_0"));
            ridgePS = new PixelShader(device, Compile("ridgePixel", "ps_4_0"));
            frameBuffer = new Buffer(device, Utilities.SizeOf<FrameConstants>(),
                ResourceUsage.Default, BindFlags.ConstantBuffer,
                CpuAccessFlags.None, ResourceOptionFlags.None, 0);

            BlendStateDescription blend = new BlendStateDescription();
            blend.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = BlendOption.One,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
                AlphaBlendOperation = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteMaskFlags.All
            };
            blendState = new BlendState(device, blend);
            rasterizerState = new RasterizerState(device, new RasterizerStateDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                IsDepthClipEnabled = false,
                IsScissorEnabled = false,
                IsMultisampleEnabled = false,
                IsAntialiasedLineEnabled = true
            });

            historyTexture = new Texture2D(device, new Texture2DDescription
            {
                Width = columns,
                Height = rows,
                ArraySize = 1,
                MipLevels = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None
            });
            historyView = new ShaderResourceView(device, historyTexture);
            paletteTexture = new Texture2D(device, new Texture2DDescription
            {
                Width = PaletteSize,
                Height = 1,
                ArraySize = 1,
                MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None
            });
            paletteView = new ShaderResourceView(device, paletteTexture);
        }

        private static byte[] Compile(string entryPoint, string profile)
        {
            IntPtr code;
            IntPtr errors;
            int length = Encoding.ASCII.GetByteCount(ShaderSource);
            int result = D3DCompile(ShaderSource, new UIntPtr((uint)length),
                "ThreeDSpectrumGpu", IntPtr.Zero, IntPtr.Zero,
                entryPoint, profile, 0, 0, out code, out errors);
            try
            {
                if (result < 0 || code == IntPtr.Zero)
                    throw new InvalidOperationException("3D shader compilation failed: " +
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

        private void EnsureOutput(SharpDX.Direct2D1.RenderTarget target, int width, int height)
        {
            if (outputBitmap != null && outputWidth == width && outputHeight == height)
                return;
            DisposeOutput();
            outputWidth = width;
            outputHeight = height;
            outputTexture = new Texture2D(device, new Texture2DDescription
            {
                Width = width,
                Height = height,
                ArraySize = 1,
                MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
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

        public void Render(SharpDX.Direct2D1.RenderTarget target, int width, int height,
            float[,] history, int head, int count, Color[] palette,
            float floorDbm, float heightRangeDb, float gridMin, float gridMax,
            int gain, int span, float fillOpacity, Color lineColor, bool gradientLine)
        {
            if (count == 0 || width < 2 || height < 2) return;
            EnsureOutput(target, width, height);
            UploadHistory(history);
            UploadPalette(palette);

            context.OutputMerger.SetRenderTargets(outputView);
            context.OutputMerger.SetBlendState(blendState);
            context.Rasterizer.State = rasterizerState;
            context.Rasterizer.SetViewport(0, 0, width, height);
            context.ClearRenderTargetView(outputView, new Color4(0f, 0f, 0f, 0f));
            context.InputAssembler.InputLayout = null;
            context.VertexShader.SetConstantBuffer(0, frameBuffer);
            context.VertexShader.SetShaderResource(0, historyView);
            context.PixelShader.SetConstantBuffer(0, frameBuffer);
            context.PixelShader.SetShaderResource(1, paletteView);

            float backWidth = 0.35f + 0.25f * span / 100f;
            float ridgeHeight = Math.Min(0.70f, 0.46f * gain / 70f);
            FrameConstants frame = new FrameConstants
            {
                Plot = new Vector4(width, height, head, rows),
                Levels = new Vector4(floorDbm, heightRangeDb, gridMin,
                    Math.Max(1f, gridMax - gridMin)),
                Shape = new Vector4(backWidth, 0.58f, ridgeHeight, 0.72f),
                Outline = new Vector4(lineColor.R / 255f, lineColor.G / 255f,
                    lineColor.B / 255f, gradientLine ? 1f : 0f)
            };
            float lineOpacity = lineColor.A / 255f;
            for (int age = Math.Min(count, rows) - 1; age >= 0; age--)
            {
                frame.Row = new Vector4(age, columns, fillOpacity, lineOpacity);
                context.UpdateSubresource(ref frame, frameBuffer);
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
                context.VertexShader.Set(fillVS);
                context.PixelShader.Set(fillPS);
                context.Draw(columns * 2, 0);
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.LineStrip;
                context.VertexShader.Set(ridgeVS);
                context.PixelShader.Set(ridgePS);
                context.Draw(columns, 0);
                if (age == 0)
                {
                    context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
                    context.VertexShader.Set(frontRibbonVS);
                    context.Draw(columns * 2, 0);
                }
            }
            context.VertexShader.SetShaderResource(0, null);
            context.PixelShader.SetShaderResource(1, null);
            context.OutputMerger.SetRenderTargets((RenderTargetView)null);
            context.Flush();
        }

        private void UploadHistory(float[,] history)
        {
            DataBox box = context.MapSubresource(historyTexture, 0,
                MapMode.WriteDiscard, MapFlags.None);
            float[] row = new float[columns];
            for (int r = 0; r < rows; r++)
            {
                System.Buffer.BlockCopy(history, r * columns * sizeof(float),
                    row, 0, columns * sizeof(float));
                Marshal.Copy(row, 0, IntPtr.Add(box.DataPointer, r * box.RowPitch), columns);
            }
            context.UnmapSubresource(historyTexture, 0);
        }

        private void UploadPalette(Color[] palette)
        {
            int hash = 17;
            for (int i = 0; i < PaletteSize; i++)
                hash = unchecked(hash * 31 + palette[i].ToArgb());
            if (hash == paletteHash) return;
            paletteHash = hash;
            byte[] pixels = new byte[PaletteSize * 4];
            for (int i = 0; i < PaletteSize; i++)
            {
                pixels[i * 4] = palette[i].B;
                pixels[i * 4 + 1] = palette[i].G;
                pixels[i * 4 + 2] = palette[i].R;
                pixels[i * 4 + 3] = 255;
            }
            DataBox box = context.MapSubresource(paletteTexture, 0,
                MapMode.WriteDiscard, MapFlags.None);
            Marshal.Copy(pixels, 0, box.DataPointer, pixels.Length);
            context.UnmapSubresource(paletteTexture, 0);
        }

        private void DisposeOutput()
        {
            if (outputBitmap != null) outputBitmap.Dispose();
            if (outputView != null) outputView.Dispose();
            if (outputTexture != null) outputTexture.Dispose();
            outputBitmap = null;
            outputView = null;
            outputTexture = null;
        }

        public void Dispose()
        {
            DisposeOutput();
            paletteView.Dispose();
            paletteTexture.Dispose();
            historyView.Dispose();
            historyTexture.Dispose();
            rasterizerState.Dispose();
            blendState.Dispose();
            frameBuffer.Dispose();
            ridgePS.Dispose();
            fillPS.Dispose();
            ridgeVS.Dispose();
            frontRibbonVS.Dispose();
            fillVS.Dispose();
        }
    }
}
