@echo off
REM ============================================================================
REM  一键：升版本号 -> 编译 -> 出产物 -> 自动快照备份
REM
REM  用法：
REM    snapshot.cmd 1.8.0.0      升到指定版本并发布
REM    snapshot.cmd patch        1.2.3 -> 1.2.4（只加修订号）
REM    snapshot.cmd minor        1.2.3 -> 1.3.0
REM    snapshot.cmd major        1.2.3 -> 2.0.0
REM
REM  快照会存到 .snapshots\v<版本>\ ，包含源码 + dll + json + README。
REM  以后要回退，直接从 .snapshots 里把源码拷回来即可（不要靠逆向改代码）。
REM ============================================================================
setlocal enabledelayedexpansion

cd /d "%~dp0"
set "SNAP=.snapshots"

REM ── 1. 决定目标版本 ──────────────────────────────────────────
set "CSPROJ=MinionHumanizer\MinionHumanizer.csproj"
for /f "tokens=2 delims=<>" %%V in ('findstr /b /c:"<Version>" "%CSPROJ%"') do set "CUR=%%V"

if "%~1"=="" (
  echo 用法： snapshot.cmd ^<版本号^> ^| patch ^| minor ^| major
  exit /b 1
)

if /i "%~1"=="patch" (
  for /f "tokens=1-3 delims=." %%A in ("%CUR%") do set "VER=%%A.%%B.%%C"
  set /a LAST=%%C+1
  for /f "tokens=1-3 delims=." %%A in ("%CUR%") do set "VER=%%A.%%B.!LAST!"
) else if /i "%~1"=="minor" (
  for /f "tokens=1-3 delims=." %%A in ("%CUR%") do set /a MINOR=%%B+1 & set "VER=%%A.!MINOR!.0"
) else if /i "%~1"=="major" (
  for /f "tokens=1-3 delims=." %%A in ("%CUR%") do set /a MAJOR=%%A+1 & set "VER=!MAJOR!.0.0"
) else (
  set "VER=%~1"
)

echo 当前版本 : %CUR%
echo 目标版本 : %VER%

REM ── 2. 写入 csproj ──────────────────────────────────────────
powershell -NoProfile -Command ^
  "$p='%CSPROJ%'; (Get-Content $p -Raw) -replace '<Version>[^<]+</Version>','<Version>%VER%</Version>' | Set-Content $p -NoNewline -Encoding UTF8"

REM ── 3. 编译 ─────────────────────────────────────────────────
echo.
echo [1/3] 编译中...
call build.cmd
if errorlevel 1 (
  echo.
  echo *** 编译失败，未生成快照。***
  exit /b 1
)

REM ── 4. 出产物 ───────────────────────────────────────────────
echo.
echo [2/3] 复制产物...
if not exist "dist\MinionHumanizer" mkdir "dist\MinionHumanizer"
copy /y "MinionHumanizer\bin\Release\MinionHumanizer.dll"   "dist\MinionHumanizer\" >nul
copy /y "MinionHumanizer\bin\Release\MinionHumanizer.json" "dist\MinionHumanizer\" >nul

REM ── 5. 快照 ─────────────────────────────────────────────────
echo.
echo [3/3] 创建快照...
if not exist "%SNAP%" mkdir "%SNAP%"
set "DEST=%SNAP%\v%VER%"
if exist "%DEST%" rmdir /s /q "%DEST%"
mkdir "%DEST%\src"
mkdir "%DEST%\dist"

xcopy /y /e /i /q "MinionHumanizer\*.cs" "%DEST%\src\" >nul
xcopy /y /e /i /q "MinionHumanizer\*.csproj" "%DEST%\src\" >nul
xcopy /y /e /i /q "dist\MinionHumanizer" "%DEST%\dist\" >nul
copy /y "README.md" "%DEST%\" >nul
copy /y "build.cmd" "%DEST%\" >nul
copy /y "build.ps1" "%DEST%\" >nul

echo.
echo ============================================================
echo  已发布 v%VER%
echo  快照： %DEST%
echo  产物： dist\MinionHumanizer\MinionHumanizer.dll
echo ============================================================
endlocal