@echo off
echo ============================================
echo Building ESPHome Fan Controller Executable
echo ============================================

echo.
echo Step 1: Checking icon files...
if not exist "logo.png" (
    echo ERROR: logo.png not found!
    pause
    exit /b 1
)
echo - logo.png found ^✓

if not exist "app_icon.ico" (
    echo - app_icon.ico not found, converting from PNG...
    python convert_icon.py
    if not exist "app_icon.ico" (
        echo ERROR: Failed to create app_icon.ico!
        pause
        exit /b 1
    )
) else (
    echo - app_icon.ico found ^✓
)

echo.
echo Step 2: Cleaning previous build...
if exist "dist" rmdir /s /q "dist"
if exist "build" rmdir /s /q "build"

echo.
echo Step 3: Verifying PyQt6 installation...
python -c "import PyQt6; print('PyQt6 found')" 2>nul
if errorlevel 1 (
    echo ERROR: PyQt6 is not installed!
    echo Please install it with: pip install PyQt6
    pause
    exit /b 1
)
echo - PyQt6 verified ^✓

echo.
echo Step 4: Building executable with PyInstaller...
echo This may take a few minutes...

pyinstaller fan_control_v1.0.1.py --noconfirm --onefile --windowed ^
 --name "ESPHome Fan Controller" ^
 --icon=app_icon.ico ^
 --add-data "logo.png;." ^
 --add-data "app_icon.ico;." ^
 --hidden-import=PyQt6.QtCore ^
 --hidden-import=PyQt6.QtGui ^
 --hidden-import=PyQt6.QtWidgets ^
 --hidden-import=aiohttp ^
 --hidden-import=aioesphomeapi ^
 --collect-all PyQt6


echo.
echo Step 5: Checking build result...
if exist "dist\ESPHome Fan Controller.exe" (
    echo ^✓ Build completed successfully!
    echo ^✓ Executable location: dist\ESPHome Fan Controller.exe
    echo.
    echo The executable should now have proper icons.
) else (
    echo ^✗ Build failed!
    echo Check the output above for PyInstaller errors.
)

echo.
pause