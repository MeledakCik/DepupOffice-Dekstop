@echo off
title Depup Office Desktop v2.2.0 - Build and Run
echo ========================================================
echo   Depup Office Desktop v2.2.0 (Microsoft Visual Studio)
echo   100%% Offline Desktop Office, PDF, Scanner ^& Vault
echo ========================================================
echo.
where dotnet >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] .NET 8.0 SDK tidak ditemukan.
    echo Silakan buka DepupOfficeDesktop.sln langsung di Microsoft Visual Studio 2022/2026
    echo atau instal .NET 8 Desktop SDK dari https://dotnet.microsoft.com/download
    pause
    exit /b 1
)
echo Building DepupOfficeDesktop...
dotnet build DepupOfficeDesktop\DepupOfficeDesktop.csproj -c Release
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Build gagal. Periksa pesan di atas.
    pause
    exit /b 1
)
echo.
echo Menjalankan Depup Office Desktop...
dotnet run --project DepupOfficeDesktop\DepupOfficeDesktop.csproj -c Release
