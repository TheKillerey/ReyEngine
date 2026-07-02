@echo off
setlocal
rem ============================================================
rem  Run the latest ReyEngine build.
rem    run.bat                 - run the latest Debug build (builds it if missing)
rem    run.bat release         - run the Release build
rem    run.bat build           - rebuild Debug first, then run
rem    run.bat release build   - rebuild Release first, then run
rem ============================================================

set "ROOT=%~dp0"
set "CONFIG=Debug"
set "DOBUILD="

for %%A in (%*) do (
  if /I "%%~A"=="release" set "CONFIG=Release"
  if /I "%%~A"=="debug"   set "CONFIG=Debug"
  if /I "%%~A"=="build"   set "DOBUILD=1"
)

set "PROJ=%ROOT%src\ReyEngine.App\ReyEngine.App.csproj"
set "EXE=%ROOT%src\ReyEngine.App\bin\%CONFIG%\net10.0\ReyEngine.App.exe"

if defined DOBUILD (
  echo [ReyEngine] Building %CONFIG% ...
  dotnet build "%PROJ%" -c %CONFIG% -nologo
  if errorlevel 1 ( echo [ReyEngine] Build FAILED. & exit /b 1 )
)

if not exist "%EXE%" (
  echo [ReyEngine] No %CONFIG% build found - building ...
  dotnet build "%PROJ%" -c %CONFIG% -nologo
  if errorlevel 1 ( echo [ReyEngine] Build FAILED. & exit /b 1 )
)

echo [ReyEngine] Launching %CONFIG% build ...
start "ReyEngine" "%EXE%"
endlocal
