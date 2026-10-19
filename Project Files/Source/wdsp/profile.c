#include "comm.h"
#include "profile.h"

volatile LONG wdsp_profile_enabled = 0;
static volatile LONG64 elapsed[MAX_CHANNELS][WDSP_PROFILE_COUNT];
static volatile LONG64 samples[MAX_CHANNELS][WDSP_PROFILE_COUNT];

void wdsp_profile_add(int channel, int category, LONGLONG started)
{
    LARGE_INTEGER ended;
    if (!started || channel < 0 || channel >= MAX_CHANNELS ||
        category < 0 || category >= WDSP_PROFILE_COUNT) return;
    QueryPerformanceCounter(&ended);
    InterlockedExchangeAdd64(&elapsed[channel][category], ended.QuadPart - started);
    InterlockedIncrement64(&samples[channel][category]);
}

PORT void SetWDSPProfileEnabled(int enabled)
{
    InterlockedExchange(&wdsp_profile_enabled, enabled ? 1 : 0);
}

/* Returns and resets one interval. The caller owns both arrays (WDSP_PROFILE_COUNT entries). */
PORT int GetWDSPProfileSnapshot(int channel, LONGLONG* ticks, LONGLONG* counts, int capacity)
{
    int i;
    if (channel < 0 || channel >= MAX_CHANNELS || !ticks || !counts ||
        capacity < WDSP_PROFILE_COUNT) return 0;
    for (i = 0; i < WDSP_PROFILE_COUNT; ++i)
    {
        ticks[i] = InterlockedExchange64(&elapsed[channel][i], 0);
        counts[i] = InterlockedExchange64(&samples[channel][i], 0);
    }
    return WDSP_PROFILE_COUNT;
}
