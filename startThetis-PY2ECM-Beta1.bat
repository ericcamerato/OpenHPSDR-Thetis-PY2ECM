@echo off
setlocal

if not exist "%~dp0Thetis.exe" (
    echo Thetis.exe nao encontrado na mesma pasta deste inicializador.
    pause
    exit /b 1
)

start "Thetis PY2ECM Beta 1" /D "%~dp0" "%~dp0Thetis.exe" -dbid:PY2ECM-WDSP210-BETA1
endlocal
