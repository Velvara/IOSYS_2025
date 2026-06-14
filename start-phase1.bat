@echo off
echo ================================================
echo  PHASE 1 — WRITE ACCESS ENABLED
echo  Git operations are blocked.
echo  File read and write are allowed.
echo ================================================
echo.
cd /d "%~dp0"
claude --permission-mode acceptEdits