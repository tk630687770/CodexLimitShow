@echo off
chcp 65001 >NUL
start "" explorer.exe "shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App"
tasklist /FI "IMAGENAME eq CodexLimitShow.exe" /NH | find /I "CodexLimitShow.exe" >NUL
if not errorlevel 1 exit /b
if exist "%~dp0正式版\CodexLimitShow.exe" (
    start "" "%~dp0正式版\CodexLimitShow.exe"
) else if exist "%~dp0CodexLimitShow.exe" (
    start "" "%~dp0CodexLimitShow.exe"
)
