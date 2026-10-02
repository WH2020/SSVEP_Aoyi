@echo off
setlocal
chcp 65001 >nul
if exist "%~dp0.tools\dotnet\dotnet.exe" (
    set "studio_dotnet=%~dp0.tools\dotnet\dotnet.exe"
) else (
    where dotnet >nul 2>nul
    if errorlevel 1 (
        echo 未找到 .NET 8 SDK。请安装 SDK，或从 GitHub Releases 下载可直接运行包。
        pause
        exit /b 1
    )
    set "studio_dotnet=dotnet"
)
"%studio_dotnet%" run --project "%~dp0src\Ssvep.Studio.App\Ssvep.Studio.App.csproj"
exit /b %errorlevel%
