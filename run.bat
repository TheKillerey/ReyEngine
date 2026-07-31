@echo off
setlocal EnableExtensions
rem ============================================================
rem  ReyEngine dev launcher.
rem
rem    run.bat                  run the latest Debug build (builds it if missing)
rem    run.bat build            rebuild Debug, then run
rem    run.bat release          run the Release build
rem    run.bat release build    rebuild Release, then run
rem    run.bat stop             just close a running ReyEngine
rem    run.bat test             run the test suite (does not launch the app)
rem    run.bat publish          self-contained win-x64 build, same as the CI release
rem    run.bat help             this text
rem
rem  A running ReyEngine holds its DLLs open, so ANY build started while it is
rem  running fails with "the file is locked by ReyEngine.App". Every build verb
rem  below closes a running instance first - that is otherwise the most common
rem  way a rebuild goes wrong.
rem ============================================================

set "ROOT=%~dp0"
set "CONFIG=Debug"
set "DOBUILD="
set "ACTION=run"

for %%A in (%*) do (
  if /I "%%~A"=="release" set "CONFIG=Release"
  if /I "%%~A"=="debug"   set "CONFIG=Debug"
  if /I "%%~A"=="build"   set "DOBUILD=1"
  if /I "%%~A"=="rebuild" set "DOBUILD=1"
  if /I "%%~A"=="stop"    set "ACTION=stop"
  if /I "%%~A"=="test"    set "ACTION=test"
  if /I "%%~A"=="publish" set "ACTION=publish"
  if /I "%%~A"=="help"    set "ACTION=help"
  if /I "%%~A"=="-h"      set "ACTION=help"
  if /I "%%~A"=="--help"  set "ACTION=help"
)

set "PROJ=%ROOT%src\ReyEngine.App\ReyEngine.App.csproj"
set "TESTPROJ=%ROOT%tests\ReyEngine.Formats.Tests\ReyEngine.Formats.Tests.csproj"
set "EXE=%ROOT%src\ReyEngine.App\bin\%CONFIG%\net10.0\ReyEngine.App.exe"
set "PUBDIR=%ROOT%publish\win-x64"

if /I "%ACTION%"=="help"    goto :help
if /I "%ACTION%"=="stop"    goto :stop
if /I "%ACTION%"=="test"    goto :test
if /I "%ACTION%"=="publish" goto :publish

rem ---------- run, building first when asked or when nothing is there ----------
if defined DOBUILD goto :dobuild
if not exist "%EXE%" (
  echo [ReyEngine] No %CONFIG% build found - building it.
  goto :dobuild
)
goto :launch

:dobuild
call :killapp
echo [ReyEngine] Building %CONFIG% ...
dotnet build "%PROJ%" -c %CONFIG% -nologo
if errorlevel 1 (
  echo [ReyEngine] Build FAILED.
  exit /b 1
)
goto :launch

:launch
if not exist "%EXE%" (
  echo [ReyEngine] Expected the app at:
  echo               %EXE%
  echo             It is not there - run "run.bat %CONFIG% build".
  exit /b 1
)
rem Print when this exe was produced. The usual confusion is launching a stale
rem build, or staring at an instance that was already open before the rebuild.
for %%F in ("%EXE%") do echo [ReyEngine] Launching %CONFIG% build, compiled %%~tF
start "ReyEngine" /D "%ROOT%" "%EXE%"
exit /b 0

rem ---------- verbs ----------
:stop
call :killapp
exit /b 0

:test
call :killapp
echo [ReyEngine] Running tests ...
dotnet test "%TESTPROJ%" -nologo
if errorlevel 1 (
  echo [ReyEngine] Tests FAILED.
  exit /b 1
)
echo [ReyEngine] Tests passed.
exit /b 0

:publish
call :killapp
echo [ReyEngine] Publishing self-contained win-x64 (same command as the CI release) ...
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true -o "%PUBDIR%" --nologo
if errorlevel 1 (
  echo [ReyEngine] Publish FAILED.
  exit /b 1
)
echo [ReyEngine] Published to %PUBDIR%
exit /b 0

:help
echo ReyEngine dev launcher
echo.
echo   run.bat                  run the latest Debug build ^(builds it if missing^)
echo   run.bat build            rebuild Debug, then run
echo   run.bat release          run the Release build
echo   run.bat release build    rebuild Release, then run
echo   run.bat stop             just close a running ReyEngine
echo   run.bat test             run the test suite
echo   run.bat publish          self-contained win-x64 build, same as the CI release
echo.
echo   Any build verb closes a running ReyEngine first - it holds its DLLs open
echo   and the build would otherwise fail on locked files.
exit /b 0

rem ---------- helper ----------
:killapp
tasklist /FI "IMAGENAME eq ReyEngine.App.exe" 2>nul | find /I "ReyEngine.App.exe" >nul
if errorlevel 1 exit /b 0
echo [ReyEngine] Closing the running instance so its DLLs are not locked ...
taskkill /IM ReyEngine.App.exe /F >nul 2>&1
rem Windows frees the file handles a moment after the process dies; without this
rem pause the very next build can still hit a locked DLL.
ping -n 2 127.0.0.1 >nul 2>&1
exit /b 0
