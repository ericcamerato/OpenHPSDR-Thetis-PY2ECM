Thetis PY2ECM v2.10.3.16 Beta 3 (x64) - local test build
Windows / .NET Framework 4.8
Expires 2026-12-30 00:00 UTC

Beta 3 adds an experimental receive-only neural CW decoder to the CWX window.
The decoder uses the DeepFist model and ONNX Runtime. It does not alter CW TX.
CWX includes a Neural CW RX checkbox, reception text and Clear RX button.
The checkbox lines up with Pause; Clear RX lines up with the space dropdown.
The tone and keying contrast are shown in the middle of the row.
Weak-signal gating and tone tracking have been adjusted after on-air testing.

This build also includes the Beta 1 and Beta 2 PY2ECM features, including
WDSP 2.10, NNR/NR5/NR6, additional PA Gain bands, per-mode DSP/AGC/ANF
memory, and the 3D Pan and 3D Panafall display modes.

Portable ZIP: extract the complete archive into a new folder. Do not replace
an installed Thetis. Run START-THETIS-BETA3.bat to use a separate test
database ID, PY2ECM-BETA3-TEST. No personal database is included.

MSI: the installer creates its own Beta 3 application folder and desktop
shortcuts for the default and HL2 databases. It may upgrade an earlier
PY2ECM MSI; back up your Thetis database before installing or testing.

This is experimental software. Verify radio setup, drive and TX behavior
before transmitting. Use at your own risk; no warranty is provided.

The model is Copyright (C) 2026 Brent Crier, N9BC, under the MIT License.
Lyra SDR is by Rick Langford, N8SDR. ONNX Runtime is Copyright (C)
Microsoft Corporation, under the MIT License. The complete model and runtime
notices are included in THIRD-PARTY-NEURAL-CW.txt and ONNX-Runtime-*.txt.
Thetis and other components retain their respective existing licenses.
