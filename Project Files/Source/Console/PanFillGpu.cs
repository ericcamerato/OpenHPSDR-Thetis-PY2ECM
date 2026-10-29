//=================================================================
// PanFillGpu.cs
// Experimental GPU opacity mask for the 2D panadapter fill.
// Copyright (C) 2026 Eric Camerato
// GPL-2.0-or-later; see the repository license.
//=================================================================

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

namespace Thetis
{
    internal sealed class PanFillGpu : IDisposable
    {
        private const string ShaderSource = @"
Texture2D<float> Samples : register(t0);
cbuffer Frame : register(b0)
{
    float4 dimensions; // width, height, columns, decimation
    float4 levels;     // grid max, dBm offset, pixels per dB, unused
};

float4 fullScreen(uint id : SV_VertexID) : SV_POSITION
{
    float2 position = id == 0 ? float2(-1.0, -1.0) :
                      id == 1 ? float2(-1.0, 3.0) : float2(3.0, -1.0);
    return float4(position, 0.0, 1.0);
}

float4 fillMask(float4 position : SV_POSITION) : SV_TARGET
{
    uint column = min((uint)(position.x / max(dimensions.w, 1.0)),
                      (uint)dimensions.z - 1);
    float dbm = Samples.Load(int3(column, 0, 0)) + levels.y;
    float top = floor((levels.x - dbm) * levels.z - 0.5);
    float coverage = position.x < dimensions.x && position.y >= top &&
                     position.y < dimensions.y ? 1.0 : 0.0;
    return float4(0.0, 0.0, 0.0, coverage);
}
";

        private const string TraceShaderSource = @"
Texture2D<float> Samples : register(t0);
cbuffer Frame : register(b0)
{
    float4 dimensions;
    float4 levels;
};

struct LineVertex { float4 position : SV_POSITION; };
LineVertex lineVertex(uint id : SV_VertexID)
{
    float dbm = Samples.Load(int3(id, 0, 0)) + levels.y;
    float y = trunc((levels.x - dbm) * levels.z - 0.5);
    float x = id * dimensions.w;
    LineVertex result;
    result.position = float4(x * 2.0 / dimensions.x - 1.0,
        1.0 - y * 2.0 / dimensions.y, 0.0, 1.0);
    return result;
}

struct StrokeVertex
{
    float4 position : SV_POSITION;
    float distance : TEXCOORD0;
};

[maxvertexcount(4)]
void expandLine(line LineVertex segment[2], inout TriangleStream<StrokeVertex> output)
{
    float2 a = float2((segment[0].position.x + 1.0) * dimensions.x * 0.5,
                      (1.0 - segment[0].position.y) * dimensions.y * 0.5);
    float2 b = float2((segment[1].position.x + 1.0) * dimensions.x * 0.5,
                      (1.0 - segment[1].position.y) * dimensions.y * 0.5);
    float2 delta = b - a;
    float2 normal = float2(-delta.y, delta.x) / max(length(delta), 0.001);
    float radius = max(levels.w, 1.0) * 0.5 + 0.5;
    float2 extension = delta / max(length(delta), 0.001) * min(radius, 1.0);
    float2 ends[2] = { a - extension, b + extension };
    StrokeVertex vertex;
    [unroll] for (int end = 0; end < 2; ++end)
    {
        [unroll] for (int side = 0; side < 2; ++side)
        {
            float sign = side == 0 ? -1.0 : 1.0;
            float2 p = ends[end] + normal * radius * sign;
            vertex.position = float4(p.x * 2.0 / dimensions.x - 1.0,
                1.0 - p.y * 2.0 / dimensions.y, 0.0, 1.0);
            vertex.distance = radius * sign;
            output.Append(vertex);
        }
    }
    output.RestartStrip();
}

float4 strokeMask(StrokeVertex input) : SV_TARGET
{
    float radius = max(levels.w, 1.0) * 0.5 + 0.5;
    float coverage = saturate(radius - abs(input.distance));
    return float4(0.0, 0.0, 0.0, coverage);
}";

        [StructLayout(LayoutKind.Sequential)]
        private struct FrameConstants
        {
            public Vector4 Dimensions;
            public Vector4 Levels;
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
        private VertexShader lineVertexShader;
        private GeometryShader lineGeometryShader;
        private PixelShader linePixelShader;
        private BlendState strokeBlendState;
        private readonly Buffer frameBuffer;
        private readonly RasterizerState rasterizerState;
        private Texture2D sampleTexture;
        private ShaderResourceView sampleView;
        private Texture2D outputTexture;
        private RenderTargetView outputView;
        private SharpDX.Direct2D1.Bitmap outputBitmap;
        private Texture2D traceTexture;
        private RenderTargetView traceView;
        private SharpDX.Direct2D1.Bitmap traceBitmap;
        private int sampleCount;
        private int outputWidth;
        private int outputHeight;
        private int traceWidth;
        private int traceHeight;

        internal SharpDX.Direct2D1.Bitmap Bitmap { get { return outputBitmap; } }
        internal SharpDX.Direct2D1.Bitmap TraceBitmap { get { return traceBitmap; } }

        internal PanFillGpu(Device d3dDevice)
        {
            if (d3dDevice.FeatureLevel < FeatureLevel.Level_10_0)
                throw new NotSupportedException("Pan fill shader requires Direct3D feature level 10.0.");
            device = d3dDevice;
            context = device.ImmediateContext;
            vertexShader = new VertexShader(device, Compile("fullScreen", "vs_4_0"));
            pixelShader = new PixelShader(device, Compile("fillMask", "ps_4_0"));
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
        }

        private static byte[] Compile(string entryPoint, string profile)
        {
            IntPtr code;
            IntPtr errors;
            string source = entryPoint == "fullScreen" || entryPoint == "fillMask"
                ? ShaderSource : TraceShaderSource;
            int length = Encoding.ASCII.GetByteCount(source);
            int result = D3DCompile(source, new UIntPtr((uint)length),
                "PanFillGpu", IntPtr.Zero, IntPtr.Zero,
                entryPoint, profile, 0, 0, out code, out errors);
            try
            {
                if (result < 0 || code == IntPtr.Zero)
                    throw new InvalidOperationException("Pan fill shader compilation failed: " +
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
                Width = columns,
                Height = 1,
                ArraySize = 1,
                MipLevels = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None
            });
            sampleView = new ShaderResourceView(device, sampleTexture);
        }

        private void EnsureOutput(SharpDX.Direct2D1.RenderTarget target, int width, int height)
        {
            if (outputBitmap != null && outputWidth == width && outputHeight == height) return;
            if (outputBitmap != null) outputBitmap.Dispose();
            if (outputView != null) outputView.Dispose();
            if (outputTexture != null) outputTexture.Dispose();
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

        private void EnsureTraceOutput(SharpDX.Direct2D1.RenderTarget target, int width, int height)
        {
            if (traceBitmap != null && traceWidth == width && traceHeight == height) return;
            if (traceBitmap != null) traceBitmap.Dispose();
            if (traceView != null) traceView.Dispose();
            if (traceTexture != null) traceTexture.Dispose();
            traceWidth = width;
            traceHeight = height;
            traceTexture = new Texture2D(device, new Texture2DDescription
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
            traceView = new RenderTargetView(device, traceTexture);
            using (var surface = traceTexture.QueryInterface<Surface>())
            {
                var properties = new SharpDX.Direct2D1.BitmapProperties(
                    new SharpDX.Direct2D1.PixelFormat(Format.B8G8R8A8_UNorm,
                        SharpDX.Direct2D1.AlphaMode.Premultiplied));
                traceBitmap = new SharpDX.Direct2D1.Bitmap(target, surface, properties);
            }
        }

        internal void RenderTrace(SharpDX.Direct2D1.RenderTarget target, int width, int height,
            float[] data, int columns, int decimation, int gridMax, float offset,
            float dbmToPixel, float lineWidth)
        {
            if (lineVertexShader == null)
            {
                lineVertexShader = new VertexShader(device, Compile("lineVertex", "vs_4_0"));
                lineGeometryShader = new GeometryShader(device, Compile("expandLine", "gs_4_0"));
                linePixelShader = new PixelShader(device, Compile("strokeMask", "ps_4_0"));
                var strokeBlend = new BlendStateDescription();
                strokeBlend.RenderTarget[0].IsBlendEnabled = true;
                strokeBlend.RenderTarget[0].SourceBlend = BlendOption.One;
                strokeBlend.RenderTarget[0].DestinationBlend = BlendOption.One;
                strokeBlend.RenderTarget[0].BlendOperation = BlendOperation.Maximum;
                strokeBlend.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
                strokeBlend.RenderTarget[0].DestinationAlphaBlend = BlendOption.One;
                strokeBlend.RenderTarget[0].AlphaBlendOperation = BlendOperation.Maximum;
                strokeBlend.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
                strokeBlendState = new BlendState(device, strokeBlend);
            }
            EnsureSamples(columns);
            EnsureTraceOutput(target, width, height);
            DataBox box = context.MapSubresource(sampleTexture, 0,
                MapMode.WriteDiscard, MapFlags.None);
            try { Marshal.Copy(data, 0, box.DataPointer, columns); }
            finally { context.UnmapSubresource(sampleTexture, 0); }

            FrameConstants frame = new FrameConstants
            {
                Dimensions = new Vector4(width, height, columns, decimation),
                Levels = new Vector4(gridMax, offset, dbmToPixel, lineWidth)
            };
            context.OutputMerger.SetRenderTargets(traceView);
            context.OutputMerger.SetBlendState(strokeBlendState);
            context.Rasterizer.State = rasterizerState;
            context.Rasterizer.SetViewport(0, 0, width, height);
            context.ClearRenderTargetView(traceView, new Color4(0f, 0f, 0f, 0f));
            context.InputAssembler.InputLayout = null;
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.LineStrip;
            context.UpdateSubresource(ref frame, frameBuffer);
            context.VertexShader.Set(lineVertexShader);
            context.VertexShader.SetConstantBuffer(0, frameBuffer);
            context.VertexShader.SetShaderResource(0, sampleView);
            context.GeometryShader.Set(lineGeometryShader);
            context.GeometryShader.SetConstantBuffer(0, frameBuffer);
            context.PixelShader.Set(linePixelShader);
            context.PixelShader.SetConstantBuffer(0, frameBuffer);
            context.Draw(columns, 0);
            context.VertexShader.SetShaderResource(0, null);
            context.GeometryShader.Set(null);
            context.OutputMerger.SetRenderTargets((RenderTargetView)null);
            context.OutputMerger.SetBlendState((BlendState)null);
            context.Flush();
        }

        internal void Render(SharpDX.Direct2D1.RenderTarget target, int width, int height,
            float[] data, int columns, int decimation, int gridMax, float offset,
            float dbmToPixel)
        {
            EnsureSamples(columns);
            EnsureOutput(target, width, height);
            DataBox box = context.MapSubresource(sampleTexture, 0,
                MapMode.WriteDiscard, MapFlags.None);
            try { Marshal.Copy(data, 0, box.DataPointer, columns); }
            finally { context.UnmapSubresource(sampleTexture, 0); }

            FrameConstants frame = new FrameConstants
            {
                Dimensions = new Vector4(width, height, columns, decimation),
                Levels = new Vector4(gridMax, offset, dbmToPixel, 0f)
            };
            context.OutputMerger.SetRenderTargets(outputView);
            context.OutputMerger.SetBlendState((BlendState)null);
            context.Rasterizer.State = rasterizerState;
            context.Rasterizer.SetViewport(0, 0, width, height);
            context.ClearRenderTargetView(outputView, new Color4(0f, 0f, 0f, 0f));
            context.InputAssembler.InputLayout = null;
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.UpdateSubresource(ref frame, frameBuffer);
            context.VertexShader.Set(vertexShader);
            context.GeometryShader.Set(null);
            context.PixelShader.Set(pixelShader);
            context.PixelShader.SetConstantBuffer(0, frameBuffer);
            context.PixelShader.SetShaderResource(0, sampleView);
            context.Draw(3, 0);
            context.PixelShader.SetShaderResource(0, null);
            context.OutputMerger.SetRenderTargets((RenderTargetView)null);
            context.Flush();
        }

        public void Dispose()
        {
            if (traceBitmap != null) traceBitmap.Dispose();
            if (traceView != null) traceView.Dispose();
            if (traceTexture != null) traceTexture.Dispose();
            if (outputBitmap != null) outputBitmap.Dispose();
            if (outputView != null) outputView.Dispose();
            if (outputTexture != null) outputTexture.Dispose();
            if (sampleView != null) sampleView.Dispose();
            if (sampleTexture != null) sampleTexture.Dispose();
            rasterizerState.Dispose();
            if (strokeBlendState != null) strokeBlendState.Dispose();
            frameBuffer.Dispose();
            if (linePixelShader != null) linePixelShader.Dispose();
            if (lineGeometryShader != null) lineGeometryShader.Dispose();
            if (lineVertexShader != null) lineVertexShader.Dispose();
            pixelShader.Dispose();
            vertexShader.Dispose();
        }
    }
}
