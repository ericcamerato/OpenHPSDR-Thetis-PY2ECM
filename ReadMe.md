# Thetis PY2ECM for Hermes Lite 2 / HL2+

Thetis PY2ECM v2.10.3.16 Beta 1 is an experimental Windows x64 build based on the final [MI0BOT Thetis HL2 v2.10.3.15](https://github.com/mi0bot/OpenHPSDR-Thetis/releases/tag/v2.10.3.15), which is derived from Thetis/OpenHPSDR. This is a PY2ECM modification, **not** an official MI0BOT or upstream release.

Download the [Beta 1 release](https://github.com/ericcamerato/OpenHPSDR-Thetis-PY2ECM/releases/tag/py2ecm-v2.10.3.16-beta1) for the x64 MSI, portable ZIP, SHA-256 checksums, and source archives. The automatically generated source ZIP and TAR.GZ are source code, not installers.

## Changes from MI0BOT v2.10.3.15

- Updated the DSP library to WDSP 2.10 and added NR5 (NNR Standard) and NR6 (NNR Premium), with an NNR settings tab immediately to the right of NR/ANF in Setup > DSP. Right-clicking NR1–NR4 opens NR/ANF, NR5–NR6 opens NNR, and NB opens its own settings.
- Added CAT and MIDI2CAT selection of NR5 and NR6. CAT commands `ZZNE` (RX1) and `ZZNF` (RX2) accept `0` for off or `1`–`6` for NR1–NR6. Existing MIDI mapping IDs remain unchanged.
- Integrated PureSignal 3.0 and corrected automatic Hermes Lite 2 feedback attenuation. Verify calibration and transmitted signal quality before regular use.
- Added independent PA Gain values for 25, 22, 19, 16, 14, 13, and 11 meters. Frequencies around 27 MHz can use the 11 m PA Gain instead of the 10 m value.
- Remembered NR selection, AGC, and ANF state separately by operating mode.
- Temporarily muted RX1/RX2 audio on TX in HL2 DIGU/DIGL, restoring the previous levels on RX to reduce transition noise. AGC Gain is not modified.
- Corrected FFT wisdom, NNR tab navigation, PureSignal, and window-title behavior.

This beta is not intended to change bandstack, filters, or unrelated RX/TX behavior. Other hardware, audio devices, and profiles have not been fully tested.

## Installation and database choices

This release requires **Windows x64 and .NET Framework 4.8**. Close Thetis before installing or replacing any files.

| Package | How to use it | Database |
| --- | --- | --- |
| x64 MSI | Install separately from MI0BOT's HL2 build. Use either the Desktop or Start Menu shortcut. | `Thetis PY2ECM Beta 1` uses the default database; `Thetis PY2ECM Beta 1-HL2` uses `-dbid:HL2`. |
| Portable x64 ZIP | Extract to a new directory and run `startThetis-PY2ECM-Beta1.bat`. Keep its `Thetis.exe` and matching `wdsp.dll` together. | Uses the separate database ID `PY2ECM-WDSP210-BETA1`. |

The installer does not migrate databases. Neither package contains personal databases, profiles, calibrations, or private scripts. If you use a customized launcher, inspect its executable path and `-dbid` argument before starting this build. Do not run two Thetis instances against the same radio or database.

## Before testing or transmitting

1. Back up your databases, TX profiles, calibrations, settings, and customized launchers. Keep a copy outside the installation directory.
2. Before TX, verify PA Gain, Drive, attenuation, ALC, RF output power, and PureSignal into a suitable load. Begin at low power and observe equipment and regulatory limits.
3. NR6 may use more CPU and may produce vibrato or other audio artifacts on some signals. Try NR5 or NR4 if needed. Audio pops and latency also depend on FPS, ASIO driver, buffers, and system load.
4. If Windows or antivirus software flags an unsigned file, verify its source and SHA-256 checksum rather than broadly disabling protection.

**Beta 1 expires on 25 December 2026 (UTC).** It does not delete databases when it expires. Install a newer build before that date.

## Disclaimer and credits

This is experimental software provided **without warranty of operation or fitness for any purpose**. Installation, use, and transmission are **at your own risk**. PY2ECM and previous contributors accept no responsibility for data loss, malfunction, equipment damage, interference, excessive power, or other losses. Back up and test carefully before using it as your primary station software.

Original licenses and attribution notices remain in place: see [LICENSE](LICENSE), [LICENSE-DUAL-LICENSING](LICENSE-DUAL-LICENSING), [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), and source headers. Credits include FlexRadio Systems, Doug Wigley/W5WC, Richard Samphire/MW0LGE, Warren Pratt/NR0V, Reid Campbell/MI0BOT, Luis Antonio Villas Boas/PY5VB (HL2+ testing), and the other contributors to Thetis/OpenHPSDR and its libraries. PY2ECM identifies the modifications in this edition, not sole authorship of the project.

## MI0BOT v2.10.3.15 baseline

- Upgraded to release 2.10.3.15 of official Thetis.
- Corrected timeout of the auto-tune routine.
- Updated the alternate RX selection form.

