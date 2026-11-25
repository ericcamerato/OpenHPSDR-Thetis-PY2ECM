/* Optional DeepFilterNet3 RX speech denoiser. The native engine and model are
 * loaded only when selected; normal RX and TX have no extra processing. */
#include "comm.h"
#include "dfnr.h"
#include <stdint.h>
#include <string.h>

typedef void DFState;
typedef DFState* (__cdecl* DF_CREATE)(const char*, float, const char*);
typedef uintptr_t (__cdecl* DF_LENGTH)(DFState*);
typedef float (__cdecl* DF_PROCESS)(DFState*, float*, float*);
typedef void (__cdecl* DF_FREE)(DFState*);
typedef void (__cdecl* DF_SET_FLOAT)(DFState*, float);

typedef struct _dfnr_state {
    int run;
    int size;
    int rate;
    double* buffer;
    DFState* model;
    DF_FREE free_model;
    DF_PROCESS process;
    DF_SET_FLOAT set_attenuation;
    DF_SET_FLOAT set_beta;
    float input[480];
    float output[480];
    float queue[960];
    int input_count;
    int queue_head;
    int queue_count;
} DFNR_STATE;

static DFNR_STATE dfnr[MAX_CHANNELS];
static HMODULE dfnr_library;
static float dfnr_attenuation[MAX_CHANNELS];
static float dfnr_beta[MAX_CHANNELS];
static int dfnr_settings_set[MAX_CHANNELS];

static int dfnr_paths(char* dll, size_t dll_len, char* model, size_t model_len)
{
    char exe[MAX_PATH];
    char* last;
    DWORD n = GetModuleFileNameA(0, exe, MAX_PATH);
    if (!n || n >= MAX_PATH) return 0;
    last = strrchr(exe, '\\');
    if (!last) return 0;
    *(last + 1) = 0;
    if (strcpy_s(dll, dll_len, exe) || strcat_s(dll, dll_len, "deepfilter.dll") ||
        strcpy_s(model, model_len, exe) ||
        strcat_s(model, model_len, "models\\DeepFilterNet3_onnx.tar.gz")) return 0;
    return 1;
}

static void reset_dfnr(DFNR_STATE* p)
{
    p->input_count = 0;
    p->queue_head = 0;
    p->queue_count = 0;
    memset(p->input, 0, sizeof(p->input));
    memset(p->output, 0, sizeof(p->output));
    memset(p->queue, 0, sizeof(p->queue));
}

void create_dfnr(int channel, int size, double* buffer, int rate)
{
    DFNR_STATE* p = &dfnr[channel];
    memset(p, 0, sizeof(*p));
    p->size = size;
    p->buffer = buffer;
    p->rate = rate;
}

/* RXA can replace midbuff without destroying the channel. Never retain its
 * old address, and never process a model at a sample rate other than 48 kHz. */
void reconfigure_dfnr(int channel, int size, double* buffer, int rate)
{
    DFNR_STATE* p = &dfnr[channel];
    p->size = size;
    p->buffer = buffer;
    p->rate = rate;
    if (rate != 48000) p->run = 0;
    reset_dfnr(p);
}

void destroy_dfnr(int channel)
{
    DFNR_STATE* p = &dfnr[channel];
    p->run = 0;
    if (p->model && p->free_model) p->free_model(p->model);
    p->model = 0;
    p->buffer = 0;
    p->rate = 0;
}

PORT int SetRXADFNSettings(int channel, float attenuation, float beta)
{
    DFNR_STATE* p;
    if (channel < 0 || channel >= MAX_CHANNELS ||
        attenuation < 0.0f || attenuation > 60.0f ||
        beta < 0.0f || beta > 0.05f) return 0;
    dfnr_attenuation[channel] = attenuation;
    dfnr_beta[channel] = beta;
    dfnr_settings_set[channel] = 1;
    p = &dfnr[channel];
    if (p->buffer) {
        EnterCriticalSection(&ch[channel].csDSP);
        if (p->model) {
            p->set_attenuation(p->model, attenuation);
            p->set_beta(p->model, beta);
        }
        LeaveCriticalSection(&ch[channel].csDSP);
    }
    return 1;
}

void flush_dfnr(int channel)
{
    reset_dfnr(&dfnr[channel]);
}

PORT int GetRXADFNAvailable(int channel)
{
    char dll[MAX_PATH], model[MAX_PATH];
    if (channel < 0 || channel >= MAX_CHANNELS || dfnr[channel].rate != 48000 ||
        !dfnr_paths(dll, sizeof(dll), model, sizeof(model))) return 0;
    return GetFileAttributesA(dll) != INVALID_FILE_ATTRIBUTES &&
           GetFileAttributesA(model) != INVALID_FILE_ATTRIBUTES;
}

PORT int SetRXADFNRun(int channel, int run)
{
    DFNR_STATE* p;
    DFState* fresh = 0;
    DFState* old = 0;
    DF_FREE free_model = 0;
    DF_PROCESS process = 0;
    DF_SET_FLOAT set_attenuation = 0;
    DF_SET_FLOAT set_beta = 0;
    HMODULE library;
    char dll[MAX_PATH], model[MAX_PATH];
    if (channel < 0 || channel >= MAX_CHANNELS) return 0;
    p = &dfnr[channel];
    if (!p->buffer || p->rate != 48000) return 0;
    if (!run) {
        EnterCriticalSection(&ch[channel].csDSP);
        p->run = 0;
        reset_dfnr(p);
        LeaveCriticalSection(&ch[channel].csDSP);
        return 1;
    }
    EnterCriticalSection(&ch[channel].csDSP);
    if (p->model) {
        if (!p->run) {
            int i;
            float silence[480] = { 0 };
            float discarded[480];
            reset_dfnr(p);
            /* Keep a warm model between NR modes, but clear short-term audio
             * history before routing live RX audio through it again. */
            for (i = 0; i < 10; ++i)
                p->process(p->model, silence, discarded);
            p->run = 1;
        }
        LeaveCriticalSection(&ch[channel].csDSP);
        return 1;
    }
    LeaveCriticalSection(&ch[channel].csDSP);
    if (!GetRXADFNAvailable(channel) ||
        !dfnr_paths(dll, sizeof(dll), model, sizeof(model))) return 0;
    library = dfnr_library ? dfnr_library : LoadLibraryA(dll);
    if (!library) return 0;
    {
        DF_CREATE create = (DF_CREATE)GetProcAddress(library, "df_create");
        DF_LENGTH length = (DF_LENGTH)GetProcAddress(library, "df_get_frame_length");
        process = (DF_PROCESS)GetProcAddress(library, "df_process_frame");
        free_model = (DF_FREE)GetProcAddress(library, "df_free");
        set_attenuation = (DF_SET_FLOAT)GetProcAddress(library, "df_set_atten_lim");
        set_beta = (DF_SET_FLOAT)GetProcAddress(library, "df_set_post_filter_beta");
        if (!create || !length || !process || !free_model || !set_attenuation || !set_beta) {
            if (!dfnr_library) FreeLibrary(library);
            return 0;
        }
        fresh = create(model, dfnr_settings_set[channel] ? dfnr_attenuation[channel] : 40.0f, 0);
        if (!fresh) {
            if (!dfnr_library) FreeLibrary(library);
            return 0;
        }
        if (length(fresh) != 480) {
            free_model(fresh);
            if (!dfnr_library) FreeLibrary(library);
            return 0;
        }
        set_beta(fresh, dfnr_settings_set[channel] ? dfnr_beta[channel] : 0.0f);
    }
    if (!dfnr_library) dfnr_library = library;
    EnterCriticalSection(&ch[channel].csDSP);
    old = p->model;
    p->model = fresh;
    p->process = process;
    p->free_model = free_model;
    p->set_attenuation = set_attenuation;
    p->set_beta = set_beta;
    reset_dfnr(p);
    p->run = 1;
    LeaveCriticalSection(&ch[channel].csDSP);
    if (old) free_model(old);
    return 1;
}

void xdfnr(int channel)
{
    DFNR_STATE* p = &dfnr[channel];
    int i;
    if (!p->run || !p->model) return;
    for (i = 0; i < p->size; ++i) {
        float sample = (float)p->buffer[2 * i];
        float enhanced = 0.0f;
        p->input[p->input_count++] = sample;
        if (p->input_count == 480) {
            int j;
            p->process(p->model, p->input, p->output);
            p->input_count = 0;
            for (j = 0; j < 480; ++j) {
                int tail = (p->queue_head + p->queue_count) % 960;
                p->queue[tail] = p->output[j];
                ++p->queue_count;
            }
        }
        if (p->queue_count) {
            enhanced = p->queue[p->queue_head];
            p->queue_head = (p->queue_head + 1) % 960;
            --p->queue_count;
        }
        p->buffer[2 * i] = enhanced;
        p->buffer[2 * i + 1] = 0.0;
    }
}
