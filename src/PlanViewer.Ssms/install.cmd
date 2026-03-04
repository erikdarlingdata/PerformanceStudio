@echo off
REM Installs the SQL Performance Studio SSMS extension.
REM For SSMS 22, this must be run from an elevated (admin) command prompt.

setlocal

set VSIX=%~dp0bin\Release\PlanViewer.Ssms.vsix
if not exist "%VSIX%" (
    set VSIX=%~dp0bin\Debug\PlanViewer.Ssms.vsix
)
if not exist "%VSIX%" (
    echo ERROR: PlanViewer.Ssms.vsix not found. Build the project first.
    echo   msbuild PlanViewer.Ssms.csproj /p:Configuration=Release
    exit /b 1
)

echo Installing SQL Performance Studio SSMS extension...
echo VSIX: %VSIX%
echo.

REM Try SSMS 22 first (per-machine install requires /admin)
set SSMS22=C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe
if exist "%SSMS22%" (
    echo Found SSMS 22 — installing with /admin flag...
    "%SSMS22%" /admin "%VSIX%"
    if %errorlevel% equ 0 (
        echo.
        echo Installed successfully into SSMS 22. Restart SSMS to activate.
    ) else (
        echo.
        echo SSMS 22 install failed. Make sure you are running as Administrator.
    )
    echo.
)

REM Try SSMS 21 (per-user install, no admin needed)
set SSMS21=C:\Program Files\Microsoft SQL Server Management Studio 21\Common7\IDE\VSIXInstaller.exe
if exist "%SSMS21%" (
    echo Found SSMS 21 — installing...
    "%SSMS21%" "%VSIX%"
    if %errorlevel% equ 0 (
        echo.
        echo Installed successfully into SSMS 21. Restart SSMS to activate.
    ) else (
        echo.
        echo SSMS 21 install failed.
    )
    echo.
)

if not exist "%SSMS22%" if not exist "%SSMS21%" (
    echo ERROR: No supported SSMS installation found.
    echo Supported: SSMS 21, SSMS 22
)

endlocal
