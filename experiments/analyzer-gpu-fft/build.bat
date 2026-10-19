@echo off
setlocal
set "VCVARS=T:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" (
    echo Visual Studio x64 compiler not found.
    exit /b 1
)
call "%VCVARS%" >nul
if errorlevel 1 exit /b 1
pushd "%~dp0"
cl /nologo /EHsc /O2 /std:c++17 /I "..\..\Project Files\Source\wdsp" benchmark.cpp /link /LIBPATH:"..\..\Project Files\lib\fftw_x64" libfftw3-3.lib
if errorlevel 1 (popd & exit /b 1)
copy /Y "..\..\Project Files\bin\x64\Release\libfftw3-3.dll" . >nul
popd
endlocal
