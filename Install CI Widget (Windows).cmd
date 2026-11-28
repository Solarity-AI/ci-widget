@echo off
rem Installs CI Widget for the current user: compiles it into %LOCALAPPDATA%\Programs\CI Widget, then the widget
rem itself runs its self-test, adds a shortcut to your Startup folder and starts. No PowerShell, no admin, no downloads.
rem If Windows or an antivirus refuses that folder, it builds into windows\bin inside this folder instead.
rem --check-sandbox only reports whether this window runs inside an app's private file copy (exit 0 = it does).
setlocal
if "%~1"=="--check-sandbox" goto checksandbox
rem A window started by a Store/MSIX app (for example a terminal inside the Claude desktop app) sees a private copy
rem of %LOCALAPPDATA% and %APPDATA%: an install from there is invisible to Windows at sign-in and dies with the
rem app. Run the install through Task Scheduler instead, which starts it outside the app.
call :sandboxed
if not errorlevel 1 goto outside
set "DIR=%LOCALAPPDATA%\Programs\CI Widget"
set "EXE=%DIR%\CIWidget.exe"
set "NEW=%DIR%\CIWidget.new.exe"
set "OLD=%DIR%\CIWidget.old.exe"
set "PREV="
rem call expands %% signs a second time, so the paths reach build.cmd through variables (a folder name may hold %).
set "BUILD=%~dp0windows\build.cmd"
echo Installing CI Widget...
rem Build next to the old exe first: a failed build must leave the running widget alone.
call "%%BUILD%%" "%%NEW%%"
if not errorlevel 1 goto built
rem An antivirus can lock a fresh exe while it scans it: wait and try once more before giving up.
echo      Retrying in 3 seconds...
call :wait 3
call "%%BUILD%%" "%%NEW%%"
if not errorlevel 1 goto built
del "%NEW%" >nul 2>&1
echo      That folder is blocked. Installing into this folder instead.
rem The widget running now may be the one in that folder: if the new one fails, that is the version to restart.
if exist "%EXE%" set "PREV=%EXE%"
set "DIR=%~dp0windows\bin"
set "EXE=%~dp0windows\bin\CIWidget.exe"
set "NEW=%~dp0windows\bin\CIWidget.new.exe"
set "OLD=%~dp0windows\bin\CIWidget.old.exe"
call "%%BUILD%%" "%%NEW%%"
if errorlevel 1 goto buildfail
:built
rem Keep a copy of the old exe until the new one passes its self-test, so a failing build can be rolled back.
del "%OLD%" >nul 2>&1
if exist "%EXE%" copy /y "%EXE%" "%OLD%" >nul 2>&1
taskkill /IM CIWidget.exe /F >nul 2>&1
move /y "%NEW%" "%EXE%" >nul 2>&1
if not errorlevel 1 goto swapped
rem The old widget can hold its exe for a moment after taskkill.
call :wait 3
move /y "%NEW%" "%EXE%" >nul 2>&1
if errorlevel 1 goto swapfail
:swapped
start "" /wait "%EXE%" --install %*
set "RC=%errorlevel%"
rem The old exe goes only once the new one passed its self-test: until then it is the way back. 0 and 4 mean it
rem passed and runs; 3 is a failed self-test, and any other code (a crash, a stopped exe) is treated the same way.
if not "%RC%"=="0" if not "%RC%"=="4" goto selftest
del "%OLD%" >nul 2>&1
if "%RC%"=="4" goto startup
echo OK   CI Widget installed and running. It sits on the taskbar left of the tray and starts with Windows.
echo      Hover it for details, click to pin the card, right-click for settings.
echo      To choose what it watches, right-click it and pick Edit config.
goto end
:selftest
if "%RC%"=="3" echo ERROR Self-test failed. Details: "%DIR%\selftest.txt"
if not "%RC%"=="3" echo ERROR Install failed (exit code %RC%).
if not exist "%OLD%" goto selftestold
move /y "%OLD%" "%EXE%" >nul 2>&1
if errorlevel 1 goto end
echo      Your previous version was put back and restarted.
start "" "%EXE%"
goto end
:selftestold
rem After the switch to windows\bin the previous version is the one left in the Programs folder: restart that one.
if not defined PREV goto end
echo      Your previous version was restarted.
start "" "%PREV%"
goto end
:startup
echo WARN CI Widget is running, but the Startup shortcut could not be created. Turn on "Start with Windows" in its menu.
goto end
:buildfail
del "%NEW%" >nul 2>&1
echo ERROR Could not build "%EXE%". Your installed widget was left as it was.
echo      If the message above says "Access is denied", your antivirus is blocking the file: add an exception
echo      for this folder, then run the installer again.
goto end
:swapfail
del "%NEW%" >nul 2>&1
del "%OLD%" >nul 2>&1
echo ERROR Could not replace "%EXE%": it is still in use. Restarting the installed widget.
if exist "%EXE%" start "" "%EXE%"
if not exist "%EXE%" if defined PREV start "" "%PREV%"
goto end

:outside
set "RUN=%~dp0windows\bin"
if not exist "%RUN%" mkdir "%RUN%"
set "LOG=%RUN%\install-outside.log"
set "DONE=%RUN%\install-outside.done"
set "WRAP=%RUN%\install-outside.cmd"
set "TASK=CI Widget install"
del "%LOG%" >nul 2>&1
del "%DONE%" >nul 2>&1
rem The wrapper holds no path: echo can turn letters the code page lacks into "?", and call reads ^ and % in a path
rem again. It finds this installer and its log from its own folder (windows\bin) instead.
> "%WRAP%" echo @echo off
>> "%WRAP%" echo set "CIW_OUTSIDE=1"
>> "%WRAP%" echo set "SELF=%%~dp0..\..\%~nx0"
>> "%WRAP%" echo call "%%%%SELF%%%%" %* ^> "%%~dp0install-outside.log" 2^>^&1
rem The end goes to its own file: a process the install left running may still hold the log for a while.
>> "%WRAP%" echo echo done^> "%%~dp0install-outside.done"
echo This window runs inside an app's private copy of your user folders; installing through Task Scheduler instead...
rem Delayed expansion on this line only, so & and ^ in the folder name reach schtasks as they are; elsewhere a ! in
rem the folder name must stay plain text.
setlocal EnableDelayedExpansion
schtasks /Create /TN "%TASK%" /TR "\"!WRAP!\"" /SC ONCE /ST 00:00 /F >nul 2>&1
endlocal
if errorlevel 1 goto outsidefail
rem /I starts the task even on battery: a task made without XML gets "start only on AC power".
schtasks /Run /I /TN "%TASK%" >nul 2>&1
if errorlevel 1 goto outsidefail
set /a "TRIES=0"
:outsidewait
call :wait 1
if exist "%DONE%" goto outsidedone
set /a "TRIES+=1"
if %TRIES% lss 600 goto outsidewait
schtasks /Delete /TN "%TASK%" /F >nul 2>&1
echo WARN The install is still running after 10 minutes. Its output: "%LOG%"
goto end
:outsidedone
schtasks /Delete /TN "%TASK%" /F >nul 2>&1
del "%WRAP%" >nul 2>&1
del "%DONE%" >nul 2>&1
rem type cannot open a path with letters outside the ANSI code page by name; findstr reads it through < instead.
findstr "^" < "%LOG%"
goto end
:outsidefail
schtasks /Delete /TN "%TASK%" /F >nul 2>&1
del "%WRAP%" >nul 2>&1
echo ERROR Could not start the install outside the app. Open this folder in File Explorer and double-click
echo      "%~nx0" instead.
goto end

:checksandbox
call :sandboxed
if errorlevel 1 (
    echo not sandboxed
    exit /b 1
)
echo sandboxed
exit /b 0

rem Exit code 0 when a new file written to %LOCALAPPDATA% lands in an app package's LocalCache instead (MSIX file
rem virtualization). The probe name is fixed so a leftover from an earlier run is removed first.
:sandboxed
if defined CIW_OUTSIDE exit /b 1
if not exist "%LOCALAPPDATA%\Packages\" exit /b 1
set "PROBE=CIWidget-sandbox-probe.tmp"
set "HIT="
del "%LOCALAPPDATA%\%PROBE%" >nul 2>&1
(echo probe) > "%LOCALAPPDATA%\%PROBE%" 2>nul
for /d %%P in ("%LOCALAPPDATA%\Packages\*") do if exist "%%~fP\LocalCache\Local\%PROBE%" set "HIT=1"
del "%LOCALAPPDATA%\%PROBE%" >nul 2>&1
if defined HIT exit /b 0
exit /b 1

rem Waits N seconds. ping, not timeout: timeout fails at once when input is redirected.
:wait
ping -n %~1 127.0.0.1 >nul 2>&1
ping -n 2 127.0.0.1 >nul 2>&1
exit /b 0

:end
if not defined CIW_OUTSIDE pause
