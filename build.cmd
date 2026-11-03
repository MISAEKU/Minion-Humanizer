@echo off
REM 用 -ExecutionPolicy Bypass 启动 build.ps1。
REM Windows 默认的 Restricted 策略下，直接双击/调用 .ps1 会被拒绝执行（exit 1），
REM 所以统一从这个 .cmd 入口进来。
REM
REM 用法：build.cmd  [Release|Debug]  [-Zip]

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set EXITCODE=%ERRORLEVEL%
endlocal & exit /b %EXITCODE%
