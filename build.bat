@echo off
setlocal
cd /d "%~dp0WinCapRecorder"

echo [1/3] Cleaning old generated files...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj

echo [2/3] Restoring packages...
dotnet restore WinCapRecorder.csproj
if errorlevel 1 goto :fail

echo [3/3] Publishing x64...
dotnet publish WinCapRecorder.csproj -c Release -r win-x64 --self-contained true
if errorlevel 1 goto :fail

echo.
echo ==========================================
echo BUILD SUCCESS
echo Output:
echo %CD%\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\WinCapRecorder.exe
echo ==========================================
exit /b 0

:fail
echo.
echo ==========================================
echo BUILD FAILED
echo ==========================================
exit /b 1
