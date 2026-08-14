@echo off
rem Сборка одного самодостаточного p2p.exe (без .NET на машине пользователя).
rem Именно publish, а не build: параметры PublishSingleFile применяются только к publish,
rem поэтому "dotnet build -c Release" всегда оставляет сотни файлов рядом с exe.
setlocal
cd /d "%~dp0"

rmdir /s /q "%~dp0publish" 2>nul
dotnet publish "%~dp0p2p.csproj" -c Release -o "%~dp0publish"
if errorlevel 1 goto :error

echo.
echo Готово: "%~dp0publish\p2p.exe"
exit /b 0

:error
echo.
echo Сборка завершилась с ошибкой.
exit /b 1
