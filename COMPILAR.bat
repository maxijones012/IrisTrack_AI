@echo off
setlocal
cd /d "%~dp0"
echo ===============================================
echo   IrisTrack AI - COMPILACION WIN-X64
echo ===============================================
where dotnet >nul 2>nul || (
  echo ERROR: No se encontro .NET SDK 8.
  echo Instala .NET 8 SDK y vuelve a ejecutar este archivo.
  pause
  exit /b 1
)
dotnet restore IrisTrackAI.sln
if errorlevel 1 goto :error
dotnet publish src\IrisTrackAI\IrisTrackAI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
if errorlevel 1 goto :error
echo.
echo LISTO: publish\win-x64\IrisTrackAI.exe
pause
exit /b 0
:error
echo.
echo ERROR DE COMPILACION.
pause
exit /b 1
