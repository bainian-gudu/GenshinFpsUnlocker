@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem =============================================================================
rem Kachina 安装包构建：
rem   1) 根目录 build.ps1 编译 Stub + Host
rem   2) kachina-builder pack → update.exe
rem   3) kachina-builder gen  → metadata + hashed
rem   4) kachina-builder pack → GenshinFpsUnlocker.Install.{ver}.exe
rem
rem 前置：
rem   - .NET 9 SDK、CMake、MSVC
rem   - kachina-builder.exe：放在 Build\ 或仓库根
rem     https://github.com/YuehaiTeam/kachina-installer/releases
rem =============================================================================

cd /d "%~dp0.."
set "ROOT=%CD%"
cd /d "%~dp0"

if exist "%~dp0dist" rd /s /q "%~dp0dist"
mkdir "%~dp0dist\GenshinFpsUnlocker" 2>nul

@echo [prepare version]
set "script=Select-String -Path '%ROOT%\src\Host\GenshinFpsUnlocker.Host.csproj' -Pattern 'Version\>(.*)\<\/Version' | ForEach-Object { $_.Matches.Groups[1].Value }"
for /f "usebackq delims=" %%i in (`powershell -NoLogo -NoProfile -Command "%script%"`) do set version=%%i
if "%version%"=="" set version=1.0.0
echo current version is %version%
if "%b%"=="" ( set "b=%version%" )

set "appDir=%~dp0dist\GenshinFpsUnlocker"
set "archiveFile=GenshinFpsUnlocker_v%b%.7z"
set "installFile=GenshinFpsUnlocker.Install.%b%.exe"

@echo [build stub + host]
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\build.ps1" -Configuration Release -SkipSetup
if errorlevel 1 (
  echo build.ps1 failed
  exit /b 1
)

@echo [stage app dir]
xcopy "%ROOT%\dist\*" "%appDir%\" /E /C /I /Y >nul
if exist "%appDir%\Setup" rd /s /q "%appDir%\Setup"
if exist "%appDir%\*.Install.*.exe" del /f /q "%appDir%\*.Install.*.exe" 2>nul
if exist "%appDir%\GenshinFpsUnlocker.update.exe" del /f /q "%appDir%\GenshinFpsUnlocker.update.exe" 2>nul
if exist "%ROOT%\LICENSE" copy /y "%ROOT%\LICENSE" "%appDir%\LICENSE" >nul
if exist "%ROOT%\config.example.json" copy /y "%ROOT%\config.example.json" "%appDir%\config.example.json" >nul

set "BUILDER="
if exist "%~dp0kachina-builder.exe" set "BUILDER=%~dp0kachina-builder.exe"
if "%BUILDER%"=="" if exist "%ROOT%\kachina-builder.exe" set "BUILDER=%ROOT%\kachina-builder.exe"
if "%BUILDER%"=="" (
  echo.
  echo kachina-builder.exe not found.
  echo Download from https://github.com/YuehaiTeam/kachina-installer/releases
  echo Place as Build\kachina-builder.exe then re-run.
  echo App payload staged at: Build\dist\GenshinFpsUnlocker\
  exit /b 1
)

@echo [kachina pack updater]
"%BUILDER%" pack -c "%~dp0kachina.config.json" -o "%appDir%\GenshinFpsUnlocker.update.exe"
if errorlevel 1 (
  echo kachina pack updater failed
  exit /b 1
)

@echo [optional portable 7z]
set "SEVEN="
where 7z >nul 2>&1 && set "SEVEN=7z"
if "%SEVEN%"=="" if exist "%ProgramFiles%\7-Zip\7z.exe" set "SEVEN=%ProgramFiles%\7-Zip\7z.exe"
if not "%SEVEN%"=="" (
  if exist "%~dp0dist\%archiveFile%" del /f /q "%~dp0dist\%archiveFile%"
  "%SEVEN%" a -t7z "%~dp0dist\%archiveFile%" "%appDir%" -mx=5 -mf=BCJ2 -r -y
)

@echo [kachina gen metadata]
cd /d "%~dp0dist"
if exist hashed rd /s /q hashed
if exist metadata.json del /f /q metadata.json
"%BUILDER%" gen -j 6 -i GenshinFpsUnlocker -m metadata.json -o hashed -r bainian-gudu/GenshinFpsUnlocker -t %b% -u ".\GenshinFpsUnlocker\GenshinFpsUnlocker.update.exe"
if errorlevel 1 (
  echo kachina gen failed
  exit /b 1
)

@echo [kachina pack offline installer]
"%BUILDER%" pack -c "%~dp0kachina.config.json" -m metadata.json -d hashed -o "%installFile%"
if errorlevel 1 (
  echo kachina pack install failed
  exit /b 1
)

echo.
echo Done:
echo   Build\dist\%installFile%
if exist "%~dp0dist\%archiveFile%" echo   Build\dist\%archiveFile%
echo   ^(updater inside portable dir: GenshinFpsUnlocker.update.exe^)
endlocal
