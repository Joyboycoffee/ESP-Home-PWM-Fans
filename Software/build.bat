@echo off
setlocal
echo =========================================================
echo  Building ESPHome Fan Controller (.NET 9 C# Standalone)
echo =========================================================
echo.

set DOTNET_EXE=%USERPROFILE%\.dotnet\dotnet.exe
if not exist "%DOTNET_EXE%" (
    where dotnet >nul 2>nul
    if %ERRORLEVEL% equ 0 (
        set DOTNET_EXE=dotnet
    ) else (
        echo [ERROR] .NET 9 SDK not found in %USERPROFILE%\.dotnet or PATH.
        pause
        exit /b 1
    )
)

echo [1/3] Restoring and Building Release binary...
cd /d "%~dp0ESPHomeFanController"
"%DOTNET_EXE%" publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Build failed! Check output above.
    pause
    exit /b 1
)

echo.
echo [2/3] Copying standalone executable to main directory...
cd /d "%~dp0"
copy /y "ESPHomeFanController\bin\Release\net9.0-windows\win-x64\publish\ESPHome Fan Controller.exe" "ESPHomeFanController.exe" >nul

echo.
echo [3/3] Build Complete!
echo Standalone executable: %~dp0ESPHomeFanController.exe
echo.
pause
