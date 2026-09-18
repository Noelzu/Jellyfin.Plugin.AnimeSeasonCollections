@echo off
setlocal
where dotnet >nul 2>nul || (
  echo ERROR: .NET 10 SDK was not found in PATH.
  echo.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
set "BUILD_EXIT=%errorlevel%"

if not "%BUILD_EXIT%"=="0" (
  echo.
  echo ============================================================
  echo BUILD FAILED - exit code %BUILD_EXIT%
  echo The window will stay open so you can read/copy the error.
  echo ============================================================
  echo.
  pause
)

exit /b %BUILD_EXIT%
