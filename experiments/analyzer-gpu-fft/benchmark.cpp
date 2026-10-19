// Optional display-analyzer experiment. This does not replace WDSP's FFTW path.
// Build x64 with build.bat and run on the same machine as the radio.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <fftw3.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>
#include <random>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3dcompiler.lib")

static const unsigned N = 4096;
static const unsigned Iterations = 300;
static const char* Shader = R"(
StructuredBuffer<float2> input : register(t0);
RWStructuredBuffer<float2> output : register(u0);
cbuffer Settings : register(b0) { uint stage; uint3 padding; };
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    uint half = 1u << stage;
    uint block = (i / (2u * half)) * 2u * half;
    uint j = i % half;
    bool upper = (i & half) != 0;
    float2 a = input[block + j];
    float2 b = input[block + j + half];
    float angle = -3.14159265358979323846f * j / half;
    float s, c;
    sincos(angle, s, c);
    float2 t = float2(c * b.x - s * b.y, s * b.x + c * b.y);
    output[i] = upper ? a - t : a + t;
}
)";
static const char* FusedShader = R"(
StructuredBuffer<float2> input : register(t0);
RWStructuredBuffer<float2> output : register(u0);
groupshared float2 scratch[4096];
[numthreads(256, 1, 1)]
void main(uint3 id : SV_GroupThreadID)
{
    for (uint lane = 0; lane < 16; ++lane) {
        uint i = id.x + lane * 256;
        scratch[i] = input[i];
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint stage = 0; stage < 12; ++stage) {
        uint half = 1u << stage;
        for (uint lane = 0; lane < 8; ++lane) {
            uint butterfly = id.x + lane * 256;
            uint block = (butterfly / half) * (2u * half);
            uint j = butterfly % half;
            float2 a = scratch[block + j];
            float2 b = scratch[block + j + half];
            float angle = -3.14159265358979323846f * j / half;
            float s, c;
            sincos(angle, s, c);
            float2 t = float2(c * b.x - s * b.y, s * b.x + c * b.y);
            scratch[block + j] = a + t;
            scratch[block + j + half] = a - t;
        }
        GroupMemoryBarrierWithGroupSync();
    }
    for (uint lane = 0; lane < 16; ++lane) {
        uint i = id.x + lane * 256;
        output[i] = scratch[i];
    }
}
)";

template <typename T> static void release(T*& p) { if (p) { p->Release(); p = nullptr; } }
static bool check(HRESULT hr, const char* where)
{
    if (SUCCEEDED(hr)) return true;
    std::fprintf(stderr, "%s failed: 0x%08x\n", where, static_cast<unsigned>(hr));
    return false;
}
static unsigned reverse12(unsigned x)
{
    unsigned r = 0;
    for (unsigned bit = 0; bit < 12; ++bit) { r = (r << 1) | (x & 1); x >>= 1; }
    return r;
}

int main()
{
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    D3D_FEATURE_LEVEL level;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION, &device, &level, &context);
    if (!check(hr, "D3D11CreateDevice")) return 1;
    if (level < D3D_FEATURE_LEVEL_11_0) {
        std::fprintf(stderr, "DirectCompute requires D3D feature level 11.0.\n");
        release(context); release(device); return 1;
    }

    ID3DBlob* compiled = nullptr;
    ID3DBlob* errors = nullptr;
    hr = D3DCompile(Shader, std::strlen(Shader), "analyzer_fft", nullptr, nullptr,
        "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &compiled, &errors);
    if (FAILED(hr)) {
        if (errors) std::fprintf(stderr, "%s\n", static_cast<const char*>(errors->GetBufferPointer()));
        check(hr, "D3DCompile"); release(errors); release(compiled);
        release(context); release(device); return 1;
    }
    ID3D11ComputeShader* shader = nullptr;
    hr = device->CreateComputeShader(compiled->GetBufferPointer(), compiled->GetBufferSize(), nullptr, &shader);
    release(errors); release(compiled);
    if (!check(hr, "CreateComputeShader")) { release(context); release(device); return 1; }
    hr = D3DCompile(FusedShader, std::strlen(FusedShader), "analyzer_fft_fused", nullptr, nullptr,
        "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &compiled, &errors);
    if (FAILED(hr)) {
        if (errors) std::fprintf(stderr, "%s\n", static_cast<const char*>(errors->GetBufferPointer()));
        check(hr, "D3DCompile fused"); release(errors); release(compiled);
        release(shader); release(context); release(device); return 1;
    }
    ID3D11ComputeShader* fusedShader = nullptr;
    hr = device->CreateComputeShader(compiled->GetBufferPointer(), compiled->GetBufferSize(), nullptr, &fusedShader);
    release(errors); release(compiled);
    if (!check(hr, "Create fused compute shader")) { release(shader); release(context); release(device); return 1; }

    ID3D11Buffer* buffers[2] = {};
    ID3D11ShaderResourceView* srvs[2] = {};
    ID3D11UnorderedAccessView* uavs[2] = {};
    ID3D11Buffer* staging = nullptr;
    ID3D11Buffer* settings = nullptr;
    D3D11_BUFFER_DESC desc = {};
    desc.ByteWidth = N * sizeof(float) * 2;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    desc.StructureByteStride = sizeof(float) * 2;
    bool ok = true;
    for (int i = 0; i < 2; ++i) {
        ok &= check(device->CreateBuffer(&desc, nullptr, &buffers[i]), "CreateBuffer");
        if (!ok) break;
        ok &= check(device->CreateShaderResourceView(buffers[i], nullptr, &srvs[i]), "CreateShaderResourceView");
        ok &= check(device->CreateUnorderedAccessView(buffers[i], nullptr, &uavs[i]), "CreateUnorderedAccessView");
        if (!ok) break;
    }
    D3D11_BUFFER_DESC stagingDesc = desc;
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.BindFlags = 0;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stagingDesc.MiscFlags = 0;
    stagingDesc.StructureByteStride = 0;
    if (ok) ok &= check(device->CreateBuffer(&stagingDesc, nullptr, &staging), "Create staging buffer");
    D3D11_BUFFER_DESC settingsDesc = {};
    settingsDesc.ByteWidth = 16;
    settingsDesc.Usage = D3D11_USAGE_DEFAULT;
    settingsDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    if (ok) ok &= check(device->CreateBuffer(&settingsDesc, nullptr, &settings), "Create settings buffer");

    std::mt19937 random(1731);
    std::normal_distribution<double> noise(0.0, 0.025);
    std::vector<double> samples(N * 2);
    std::vector<float> upload(N * 2);
    for (unsigned i = 0; i < N; ++i) {
        double angle = 2.0 * 3.14159265358979323846 * 317.0 * i / N;
        samples[i * 2] = std::cos(angle) + noise(random);
        samples[i * 2 + 1] = std::sin(angle) + noise(random);
    }
    fftw_complex* cpuIn = static_cast<fftw_complex*>(fftw_malloc(sizeof(fftw_complex) * N));
    fftw_complex* cpuOut = static_cast<fftw_complex*>(fftw_malloc(sizeof(fftw_complex) * N));
    fftw_plan plan = nullptr;
    if (cpuIn && cpuOut) plan = fftw_plan_dft_1d(N, cpuIn, cpuOut, FFTW_FORWARD, FFTW_MEASURE);
    if (!plan) ok = false;

    auto gpuFft = [&]() -> bool {
        for (unsigned i = 0; i < N; ++i) {
            unsigned destination = reverse12(i);
            upload[destination * 2] = static_cast<float>(samples[i * 2]);
            upload[destination * 2 + 1] = static_cast<float>(samples[i * 2 + 1]);
        }
        context->UpdateSubresource(buffers[0], 0, nullptr, upload.data(), 0, 0);
        context->CSSetShader(shader, nullptr, 0);
        context->CSSetConstantBuffers(0, 1, &settings);
        for (unsigned stage = 0; stage < 12; ++stage) {
            unsigned in = stage & 1;
            unsigned out = in ^ 1;
            unsigned constants[4] = { stage, 0, 0, 0 };
            context->UpdateSubresource(settings, 0, nullptr, constants, 0, 0);
            context->CSSetShaderResources(0, 1, &srvs[in]);
            context->CSSetUnorderedAccessViews(0, 1, &uavs[out], nullptr);
            context->Dispatch(N / 256, 1, 1);
            ID3D11ShaderResourceView* nullSrv = nullptr;
            ID3D11UnorderedAccessView* nullUav = nullptr;
            context->CSSetShaderResources(0, 1, &nullSrv);
            context->CSSetUnorderedAccessViews(0, 1, &nullUav, nullptr);
        }
        context->CopyResource(staging, buffers[0]);
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        HRESULT result = context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped);
        if (!check(result, "Map FFT output")) return false;
        std::memcpy(upload.data(), mapped.pData, desc.ByteWidth);
        context->Unmap(staging, 0);
        return true;
    };
    auto fusedGpuFft = [&]() -> bool {
        for (unsigned i = 0; i < N; ++i) {
            unsigned destination = reverse12(i);
            upload[destination * 2] = static_cast<float>(samples[i * 2]);
            upload[destination * 2 + 1] = static_cast<float>(samples[i * 2 + 1]);
        }
        context->UpdateSubresource(buffers[0], 0, nullptr, upload.data(), 0, 0);
        context->CSSetShader(fusedShader, nullptr, 0);
        context->CSSetShaderResources(0, 1, &srvs[0]);
        context->CSSetUnorderedAccessViews(0, 1, &uavs[1], nullptr);
        context->Dispatch(1, 1, 1);
        ID3D11ShaderResourceView* nullSrv = nullptr;
        ID3D11UnorderedAccessView* nullUav = nullptr;
        context->CSSetShaderResources(0, 1, &nullSrv);
        context->CSSetUnorderedAccessViews(0, 1, &nullUav, nullptr);
        context->CopyResource(staging, buffers[1]);
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        HRESULT result = context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped);
        if (!check(result, "Map fused FFT output")) return false;
        std::memcpy(upload.data(), mapped.pData, desc.ByteWidth);
        context->Unmap(staging, 0);
        return true;
    };
    if (ok) {
        std::memcpy(cpuIn, samples.data(), sizeof(fftw_complex) * N);
        fftw_execute(plan);
        ok = gpuFft();
    }
    if (ok) {
        double worstAbsolute = 0.0;
        double rms = 0.0;
        for (unsigned i = 0; i < N; ++i) {
            double realError = static_cast<double>(upload[i * 2]) - cpuOut[i][0];
            double imagError = static_cast<double>(upload[i * 2 + 1]) - cpuOut[i][1];
            worstAbsolute = std::max(worstAbsolute, std::hypot(realError, imagError));
            rms += realError * realError + imagError * imagError;
        }
        rms = std::sqrt(rms / N);
        std::printf("FFT size: %u, complex, %u iterations\n", N, Iterations);
        std::printf("GPU float vs FFTW double: max bin error %.6g, RMS %.6g\n", worstAbsolute, rms);
        if (worstAbsolute > 0.5) {
            std::fprintf(stderr, "Validation failed; timing would not be meaningful.\n");
            ok = false;
        }
    }
    if (ok) {
        ok = fusedGpuFft();
        double worstAbsolute = 0.0;
        for (unsigned i = 0; i < N; ++i) {
            double realError = static_cast<double>(upload[i * 2]) - cpuOut[i][0];
            double imagError = static_cast<double>(upload[i * 2 + 1]) - cpuOut[i][1];
            worstAbsolute = std::max(worstAbsolute, std::hypot(realError, imagError));
        }
        std::printf("Fused GPU float vs FFTW double: max bin error %.6g\n", worstAbsolute);
        if (worstAbsolute > 0.5) ok = false;
    }
    if (ok) {
        auto start = std::chrono::steady_clock::now();
        for (unsigned iteration = 0; iteration < Iterations; ++iteration) {
            std::memcpy(cpuIn, samples.data(), sizeof(fftw_complex) * N);
            fftw_execute(plan);
            volatile double consumed = cpuOut[iteration % N][0];
            (void)consumed;
        }
        auto cpuEnd = std::chrono::steady_clock::now();
        for (unsigned iteration = 0; iteration < Iterations; ++iteration)
            if (!gpuFft()) { ok = false; break; }
        auto gpuEnd = std::chrono::steady_clock::now();
        for (unsigned iteration = 0; iteration < Iterations; ++iteration)
            if (!fusedGpuFft()) { ok = false; break; }
        auto fusedEnd = std::chrono::steady_clock::now();
        double cpuUs = std::chrono::duration<double, std::micro>(cpuEnd - start).count() / Iterations;
        double gpuUs = std::chrono::duration<double, std::micro>(gpuEnd - cpuEnd).count() / Iterations;
        double fusedUs = std::chrono::duration<double, std::micro>(fusedEnd - gpuEnd).count() / Iterations;
        if (ok) {
            std::printf("FFTW CPU (input copy + FFT): %.2f us/frame\n", cpuUs);
            std::printf("GPU end-to-end (upload + 12 stages + readback): %.2f us/frame\n", gpuUs);
            std::printf("GPU fused end-to-end (upload + 1 dispatch + readback): %.2f us/frame\n", fusedUs);
            std::printf("GPU / FFTW wall-time ratio: %.2fx\n", gpuUs / cpuUs);
            std::printf("Fused GPU / FFTW wall-time ratio: %.2fx\n", fusedUs / cpuUs);
        }
    }
    if (plan) fftw_destroy_plan(plan);
    if (cpuIn) fftw_free(cpuIn);
    if (cpuOut) fftw_free(cpuOut);
    release(settings); release(staging);
    for (int i = 0; i < 2; ++i) { release(uavs[i]); release(srvs[i]); release(buffers[i]); }
    release(fusedShader); release(shader); release(context); release(device);
    return ok ? 0 : 1;
}
