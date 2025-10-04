# -*- mode: python ; coding: utf-8 -*-

# Simplified and portable PyInstaller spec file for ESPHome Fan Controller

a = Analysis(
    ['fan_control_app_optimized.py'],
    pathex=['.'],
    binaries=[],
    datas=[
        ('logo.png', '.'),
        ('app_icon.ico', '.')
    ],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name='ESPHome Fan Controller',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    icon='app_icon.ico'
)
