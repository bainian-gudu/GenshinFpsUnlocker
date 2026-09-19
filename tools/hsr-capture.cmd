@echo off
rem 崩坏：星穹铁道采集脚本的免命令入口：双击即可，不需要安装任何东西。
rem 用 Windows 自带的 Windows PowerShell 5.1 运行同目录的 hsr-capture.ps1。
chcp 65001 >nul 2>&1
setlocal
rem 双击 UNC 路径（\\wsl.localhost\...）时 cmd 的当前目录不可用，pushd 会临时映射盘符消掉警告
pushd "%~dp0" 2>nul
set "SCRIPT=%~dp0hsr-capture.ps1"

if not exist "%SCRIPT%" (
    echo 找不到 hsr-capture.ps1：%SCRIPT%
    pause
    exit /b 2
)

echo 正在采集崩坏：星穹铁道的信息（只读，不改游戏目录、不写注册表）...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "CODE=%ERRORLEVEL%"

echo.
if "%CODE%"=="0" (
    echo 采集完成。结果在脚本上一级的 hsr-capture 目录里。
) else (
    echo 采集脚本退出码：%CODE%（未找到游戏时请用 -GamePath 指定 StarRail.exe）
)
pause
popd 2>nul
exit /b %CODE%
