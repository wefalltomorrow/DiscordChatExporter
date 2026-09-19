@echo off
rem One-click entry point: double-click this file, or run it with the same
rem arguments build.ps1 accepts, e.g.  build.cmd -Target Cli -Zip
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Pause %*
