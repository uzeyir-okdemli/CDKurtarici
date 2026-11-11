@echo off
chcp 65001 >nul
title CD/DVD Tam Performans Veri Kurtarici
if exist "%~dp0CDKurtarici.exe" (
    start "" "%~dp0CDKurtarici.exe"
) else (
    start "" "%~dp0bin\Release\net8.0-windows\CDKurtarici.exe"
)
