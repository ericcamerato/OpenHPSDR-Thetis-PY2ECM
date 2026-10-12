Thetis PY2ECM WDSP 2.10 v2.10.3.16 Final (x64)
Windows / .NET Framework 4.8
No time-based expiration.

This is a PY2ECM modification of the MI0BOT Thetis HL2 v2.10.3.15
final source, not an official MI0BOT or upstream release. It includes
WDSP 2.10, PureSignal 3.0, NR5/NR6, expanded PA Gain bands, per-mode
DSP/AGC/ANF memory, 3D Pan/Panafall, and receive-only neural CW decoding.
The neural CW model and ONNX Runtime run on the CPU. CW TX is unchanged.

Portable ZIP: extract every file to a new directory. Run
START-THETIS-WDSP210.bat to use database ID PY2ECM-WDSP210 and enable
Neural CW RX. Do not replace an installed Thetis. No personal database,
profiles, calibrations, or private scripts are included. If you used a
custom -datapath in a previous build, add it to your own launcher to
continue with the same database location. Back up before changing paths.

MSI: creates shortcuts for the default and HL2 databases. It may upgrade
an earlier PY2ECM MSI with the same installer identity. Close Thetis and
back up your databases and TX profiles before installing. Confirm the
shortcut target and database ID before starting the radio.

Before transmitting, verify PA Gain, Drive, RF power, ALC, attenuation,
and PureSignal calibration into a suitable load. Use at your own risk;
no warranty is provided. Read the full ReadMe.md and license/third-party
notices in the source repository for changes, credits, and limitations.
