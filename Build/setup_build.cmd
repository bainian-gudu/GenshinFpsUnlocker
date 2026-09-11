@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem =============================================================================
rem 安装包构建（与常见 MicaSetup 流程一致）：
rem   1) 调用根目录 build.ps1 编译 Stub + Host（跳过旧 WinForms Setup）
rem   2) 7z 打 publish.7z 便携/安装载荷
rem   3) makemica + micasetup.json → GenshinFpsUnlocker_Setup_v{ver}.exe
rem      （内含 Uninst.exe、桌面/开始菜单快捷方式、ARP 注册表）
rem
rem 前置：
rem   - .NET 9 SDK、CMake、MSVC
rem   - 7-Zip（PATH 中的 7z，或 Build\MicaSetup.Tools\7-Zip\7z.exe）
rem   - makemica.exe：从 https://github.com/lemutec/MicaSetup/releases
rem     下载 MicaSetup_v*.7z 解压到本目录（含 makemica.exe 与 template\）
rem =============================================================================

cd /d "%~dp0.."
set "ROOT=%CD%"
cd /d "%~dp0"

if exist "%~dp0dist" rd /s /q "%~dp0dist"
mkdir "%~dp0dist\payload" 2>nul

@echo [prepare version]
set "script=Select-String -Path '%ROOT%\src\Host\GenshinFpsUnlocker.Host.csproj' -Pattern 'Version\>(.*)\<\/Version' | ForEach-Object { $_.Matches.Groups[1].Value }"
for /f "usebackq delims=" %%i in (`powershell -NoLogo -NoProfile -Command "%script%"`) do set version=%%i
if "%version%"=="" set version=1.0.0
echo current version is %version%
if "%b%"=="" ( set "b=%version%" )

set "tmpfolder=%~dp0dist\payload"
set "archiveFile=GenshinFpsUnlocker_v%b%.7z"
set "setupFile=GenshinFpsUnlocker_Setup_v%b%.exe"

@echo [build stub + host]
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\build.ps1" -Configuration Release -SkipSetup
if errorlevel 1 (
  echo build.ps1 failed
  exit /b 1
)

@echo [stage payload from %ROOT%\dist]
xcopy "%ROOT%\dist\*" "%tmpfolder%\" /E /C /I /Y >nul
if exist "%tmpfolder%\Setup" rd /s /q "%tmpfolder%\Setup"
if exist "%tmpfolder%\*.Setup.exe" del /f /q "%tmpfolder%\*.Setup.exe" 2>nul
if exist "%ROOT%\LICENSE" copy /y "%ROOT%\LICENSE" "%tmpfolder%\LICENSE" >nul
if exist "%ROOT%\config.example.json" copy /y "%ROOT%\config.example.json" "%tmpfolder%\config.example.json" >nul

@echo [pack publish.7z]
set "SEVEN="
where 7z >nul 2>&1 && set "SEVEN=7z"
if "%SEVEN%"=="" if exist "%~dp0MicaSetup.Tools\7-Zip\7z.exe" set "SEVEN=%~dp0MicaSetup.Tools\7-Zip\7z.exe"
if "%SEVEN%"=="" (
  echo ERROR: 7z not found. Install 7-Zip or place 7z.exe under Build\MicaSetup.Tools\7-Zip\
  exit /b 1
)

if exist "%~dp0publish.7z" del /f /q "%~dp0publish.7z"
"%SEVEN%" a -t7z "%~dp0publish.7z" "%tmpfolder%\*" -mx=5 -mf=BCJ2 -r -y
if errorlevel 1 exit /b 1
copy /y "%~dp0publish.7z" "%~dp0dist\%archiveFile%" >nul

@echo [MicaSetup makemica]
if not exist "%~dp0makemica.exe" (
  echo.
  echo makemica.exe not found in Build\.
  echo Download latest MicaSetup_v*.7z from:
  echo   https://github.com/lemutec/MicaSetup/releases
  echo Extract into Build\ (makemica.exe + template\), then re-run.
  echo Portable archive is ready: Build\dist\%archiveFile%
  exit /b 0
)

copy /y "%~dp0micasetup.json" "%~dp0micasetup.json.bak" >nul 2>&1
if exist "%ROOT%\LICENSE" copy /y "%ROOT%\LICENSE" "%~dp0LICENSE" >nul
"%~dp0makemica.exe" "%~dp0micasetup.json"
if errorlevel 1 (
  echo makemica failed
  exit /b 1
)

if exist "%~dp0GenshinFpsUnlocker_Setup.exe" (
  move /y "%~dp0GenshinFpsUnlocker_Setup.exe" "%~dp0dist\%setupFile%" >nul
  echo.
  echo Done:
  echo   Build\dist\%archiveFile%
  echo   Build\dist\%setupFile%
) else (
  echo WARNING: GenshinFpsUnlocker_Setup.exe not produced
  dir /b "%~dp0*.exe" 2>nul
  exit /b 1
)

endlocal
