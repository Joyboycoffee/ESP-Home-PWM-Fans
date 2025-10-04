@echo off
setlocal
title ESPHome Fan Controller Builder

echo ============================================
echo Building ESPHome Fan Controller Executable
echo ============================================
echo.

:: Step 1: Check icon files
echo Step 1: Checking icon files...
if not exist "logo.png" (
    echo ERROR: logo.png not found!
    pause
    exit /b 1
)
echo - logo.png found ✓

if not exist "app_icon.ico" (
    echo ERROR: app_icon.ico not found!
    pause
    exit /b 1
)
echo - app_icon.ico found ✓
echo.

:: Step 2: Clean previous build
echo Step 2: Cleaning previous build...
if exist "dist" rmdir /s /q "dist"
if exist "build" rmdir /s /q "build"
echo.

:: Step 3: Build with PyInstaller
echo Step 3: Building executable with PyInstaller...
pyinstaller --noconfirm "ESPHome Fan Controller.spec"
if %errorlevel% neq 0 (
    echo ✗ Build failed!
    pause
    exit /b 1
)
echo.

:: Step 4: Check build result
echo Step 4: Checking build result...
if exist "dist\ESPHome Fan Controller.exe" (
    echo ✓ Build completed successfully!
    echo ✓ Executable location: dist\ESPHome Fan Controller.exe
) else (
    echo ✗ Build failed! Check logs above.
)
echo.
pause
endlocal
