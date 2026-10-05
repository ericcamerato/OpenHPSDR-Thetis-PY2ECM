@echo off
setlocal EnableExtensions

set "THETIS_DIR=T:\RadioDev\ThetisDev\OpenHPSDR-Thetis\outputs\PY2ECM-v2.10.3.16-Beta3-Eric-Local"
set "THETIS_DATA=T:\RadioDev\ThetisDev\OpenHPSDR-Thetis\outputs\PY2ECM-v2.10.3.16-Beta3-Eric-Local\user-data"
set "MUTE_STARTED=0"

if not exist "%THETIS_DIR%\Thetis.exe" (
    echo Thetis Beta 3 was not found in: %THETIS_DIR%
    pause
    exit /b 1
)
if not exist "%THETIS_DIR%\models\deepfist.onnx" (
    echo The neural CW model was not found in: %THETIS_DIR%\models
    pause
    exit /b 1
)
if not exist "%THETIS_DATA%" mkdir "%THETIS_DATA%"

powercfg -setactive SCHEME_MIN

tasklist | find /i "pythonw.exe" >nul 2>&1
if errorlevel 1 (
    if exist "T:\PythonScripts\keyboard_py\startmute.bat" (
        start "" "T:\PythonScripts\keyboard_py\startmute.bat"
        set "MUTE_STARTED=1"
    )
)

start "Thetis PY2ECM Beta 3" /WAIT /MAX /HIGH /D "%THETIS_DIR%" "%THETIS_DIR%\Thetis.exe" "-datapath:%THETIS_DATA%" -dbid:PY2ECM-WDSP210 -cmasioconfig -neuralcwrx

if "%MUTE_STARTED%"=="1" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'python.exe' -or $_.Name -eq 'pythonw.exe') -and $_.CommandLine -match 'start_key\.py' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"
)

powercfg -setactive SCHEME_BALANCED

if exist "T:\PythonScripts\DisableLPF.ps1" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "T:\PythonScripts\DisableLPF.ps1"
)

endlocal
