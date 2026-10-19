@echo off
setlocal
set "THETIS_WDSP_PROFILE=1"
call "T:\PythonScripts\startThetisPY2ECM-v2.10.4.1.bat"
echo.
echo WDSP profile CSV files:
dir /b "T:\RadioDev\ThetisDev\OpenHPSDR-Thetis\outputs\PY2ECM-v2.10.4.1-GPU-Preview\wdsp-profile-*.csv"
endlocal
