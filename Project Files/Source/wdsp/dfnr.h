#ifndef WDSP_DFNR_H
#define WDSP_DFNR_H

void create_dfnr(int channel, int size, double* buffer, int rate);
void reconfigure_dfnr(int channel, int size, double* buffer, int rate);
void destroy_dfnr(int channel);
void flush_dfnr(int channel);
void xdfnr(int channel);

#endif
