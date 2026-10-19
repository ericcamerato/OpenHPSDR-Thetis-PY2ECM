/* Opt-in, lock-free timing counters for comparing RX and display DSP work. */
#ifndef WDSP_PROFILE_H
#define WDSP_PROFILE_H

#include <windows.h>

enum wdsp_profile_category
{
    WDSP_PROFILE_RX_LOOP,
    WDSP_PROFILE_RX_FRONTEND,
    WDSP_PROFILE_RX_DEMOD,
    WDSP_PROFILE_RX_NOISE,
    WDSP_PROFILE_RX_FILTER_AGC,
    WDSP_PROFILE_RX_POST,
    WDSP_PROFILE_SPECTRUM_TOTAL,
    WDSP_PROFILE_SPECTRUM_FFT,
    WDSP_PROFILE_SPECTRUM_POST,
    WDSP_PROFILE_COUNT
};

extern volatile LONG wdsp_profile_enabled;

static __inline LONGLONG wdsp_profile_now(void)
{
    LARGE_INTEGER value;
    if (!wdsp_profile_enabled) return 0;
    QueryPerformanceCounter(&value);
    return value.QuadPart;
}

void wdsp_profile_add(int channel, int category, LONGLONG started);

#endif
