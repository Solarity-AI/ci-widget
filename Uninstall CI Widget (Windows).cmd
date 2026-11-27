@echo off
rem Removes CI Widget: its Startup shortcut and its program folder. Your config (%USERPROFILE%\.ci-widget) stays,
rem so a reinstall keeps it; delete that folder by hand to remove it too. No PowerShell, no admin.
setlocal
set "APPDIR=%LOCALAPPDATA%\Programs\CI Widget"
echo Removing CI Widget...
taskkill /IM CIWidget.exe /F >nul 2>&1
set "EXE=%APPDIR%\CIWidget.exe"
if exist "%EXE%" goto run
set "EXE=%~dp0windowsin\CIWidget.exe"
if exist "%EXE%" goto run
goto manual
:run
start "" /wait "%EXE%" --uninstall
if "%errorlevel%"=="0" goto files
:manual
del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\CI Widget.lnk" >nul 2>&1
:files
if exist "%APPDIR%" rmdir /s /q "%APPDIR%"
if exist "%~dp0windowsin\CIWidget.exe" del "%~dp0windowsin\CIWidget.exe" >nul 2>&1
echo OK   CI Widget removed. Your config in "%USERPROFILE%\.ci-widget" was kept.
pause
