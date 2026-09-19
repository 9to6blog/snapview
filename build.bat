@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

if /i "%~1"=="release"   goto release
if /i "%~1"=="installer" goto installer
if /i "%~1"=="selftest"  goto selftest
if /i "%~1"=="icon"      goto icon
if /i "%~1"=="clean"     goto clean

echo [debug build]
dotnet build -c Debug --nologo
goto end

:release
echo [release - single exe]
call :publishapp
if errorlevel 1 goto end
echo.
echo   -^> %CD%\dist\SnapView.exe
echo   needs .NET 8 Desktop Runtime. use --self-contained true to bundle it.
goto end

:installer
echo [installer]
call :publishapp
if errorlevel 1 goto end
if exist setup rmdir /s /q setup
dotnet publish installer\SnapViewSetup.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o setup --nologo
if errorlevel 1 goto end
echo.
echo   -^> %CD%\setup\SnapView-Setup.exe
goto end

:selftest
echo [self test - no window is shown]
dotnet run --project tests\SelfTest -c Release --nologo
goto end

:icon
echo [regenerate assets\app.ico]
dotnet run --project tools\IconGen -c Release --nologo
goto end

:clean
if exist bin   rmdir /s /q bin
if exist obj   rmdir /s /q obj
if exist dist  rmdir /s /q dist
if exist setup rmdir /s /q setup
if exist tests\SelfTest\bin rmdir /s /q tests\SelfTest\bin
if exist tests\SelfTest\obj rmdir /s /q tests\SelfTest\obj
if exist tools\IconGen\bin  rmdir /s /q tools\IconGen\bin
if exist tools\IconGen\obj  rmdir /s /q tools\IconGen\obj
if exist installer\bin rmdir /s /q installer\bin
if exist installer\obj rmdir /s /q installer\obj
echo cleaned.
goto end

rem ---- publish the app itself into dist\ ----
rem if it is running, the exe is locked; rename instead of delete
:publishapp
if exist dist\SnapView.old.exe del /q dist\SnapView.old.exe >nul 2>&1
if exist dist\SnapView.exe move /y dist\SnapView.exe dist\SnapView.old.exe >nul 2>&1
dotnet publish SnapView.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist --nologo
if errorlevel 1 exit /b %errorlevel%

exit /b 0

:end
endlocal
