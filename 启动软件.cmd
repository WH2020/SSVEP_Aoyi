@echo off
setlocal
chcp 65001 >nul
set "app_dir=%~dp0outputs\SSVEP-Studio"
if not exist "%app_dir%\Ssvep.Studio.exe" (
    echo 未找到软件，请保留项目中的 outputs\SSVEP-Studio 完整文件夹。
    pause
    exit /b 1
)
start "" /d "%app_dir%" "%app_dir%\Ssvep.Studio.exe"
exit /b %errorlevel%
