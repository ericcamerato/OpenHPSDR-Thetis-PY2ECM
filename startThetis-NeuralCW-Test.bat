@echo off
setlocal
set "THEtisTestExe=%~dp0Project Files\bin\x64\Release\Thetis.exe"
set "THEtisTestData=%~dp0neural-cw-test-data"

if not exist "%THEtisTestExe%" (
    echo Thetis test build not found: "%THEtisTestExe%"
    pause
    exit /b 1
)

if not exist "%THEtisTestData%" mkdir "%THEtisTestData%"
start "Thetis Neural CW RX Test" /D "%~dp0Project Files\bin\x64\Release" "%THEtisTestExe%" "-datapath:%THEtisTestData%"
endlocal
