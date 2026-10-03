@echo off
setlocal
chcp 65001 >nul
set "app_dir=%~dp0outputs\SSVEP-Studio-Research"
if not exist "%app_dir%\Ssvep.Studio.exe" (
    echo 未找到参数研发版。请先运行 tools\publish-research.ps1 生成完整运行包。
    pause
    exit /b 1
)
start "" /d "%app_dir%" "%app_dir%\Ssvep.Studio.exe" --research
exit /b %errorlevel%
