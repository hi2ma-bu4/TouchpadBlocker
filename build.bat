@echo off
setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo csc.exe が見つかりません。
    pause
    exit /b 1
)

echo Compiling...

"%CSC%" ^
    /target:exe ^
    /out:"%~dp0TouchpadBlocker.exe" ^
    /optimize+ ^
    "%~dp0Program.cs"

if errorlevel 1 (
    echo.
    echo コンパイルに失敗しました。
    pause
    exit /b 1
)

echo.
echo コンパイル成功:
echo %~dp0TouchpadBlocker.exe
pause