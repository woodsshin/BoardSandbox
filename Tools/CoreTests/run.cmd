@echo off
setlocal
set "root=%~dp0..\.."
if not defined BSE_NATIVE_DIR set "BSE_NATIVE_DIR=%root%\Assets\BoardSandbox\Plugins\Windows\x86_64"
if not defined BSE_MODS_DIR set "BSE_MODS_DIR=%root%\Assets\StreamingAssets\Mods"
set "PATH=%BSE_NATIVE_DIR%;%PATH%"
dotnet run --project "%~dp0CoreTests.csproj" -c Release -- %*
exit /b %ERRORLEVEL%
