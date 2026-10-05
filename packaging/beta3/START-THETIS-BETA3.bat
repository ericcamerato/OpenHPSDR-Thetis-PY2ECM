@echo off
setlocal
cd /d "%~dp0"

if not exist "Thetis.exe" (
    echo Thetis.exe was not found. Extract the complete ZIP before starting.
    pause
    exit /b 1
)
if not exist "models\deepfist.onnx" (
    echo The neural CW model was not found. Extract the complete ZIP.
    pause
    exit /b 1
)

start "Thetis PY2ECM Beta 3" /D "%~dp0" "%~dp0Thetis.exe" -dbid:PY2ECM-BETA3-TEST -neuralcwrx
endlocal
