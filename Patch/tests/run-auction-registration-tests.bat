@echo off
setlocal

for %%I in ("%ProgramFiles(x86)%") do set "AUCTION_PROGRAM_FILES_X86=%%~sI"
set "AUCTION_VSWHERE=%AUCTION_PROGRAM_FILES_X86%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%AUCTION_VSWHERE%" (
    echo Visual Studio locator not found: %AUCTION_VSWHERE%
    exit /b 1
)

set "AUCTION_VS_PATH="
for /f "usebackq tokens=*" %%I in (`"%AUCTION_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
    set "AUCTION_VS_PATH=%%I"
)
if not defined AUCTION_VS_PATH (
    echo Visual C++ toolchain not found.
    exit /b 1
)

call "%AUCTION_VS_PATH%\Common7\Tools\VsDevCmd.bat" -arch=x86 -host_arch=x64 >nul
if errorlevel 1 exit /b %errorlevel%

set "AUCTION_TEST_STEM=%TEMP%\AuctionRegistrationAckFixTests-%RANDOM%"
cl /nologo /EHsc /std:c++17 tests\AuctionRegistrationAckFixTests.cpp /Fo:"%AUCTION_TEST_STEM%.obj" /Fe:"%AUCTION_TEST_STEM%.exe"
if errorlevel 1 exit /b %errorlevel%

"%AUCTION_TEST_STEM%.exe"
set "AUCTION_TEST_RESULT=%errorlevel%"
del /q "%AUCTION_TEST_STEM%.exe" "%AUCTION_TEST_STEM%.obj" >nul 2>&1
exit /b %AUCTION_TEST_RESULT%
