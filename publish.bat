@echo off
chcp 65001 >nul
echo Сборка DreamIsland...
taskkill /IM DreamIsland.exe /F >nul 2>&1
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo.
  echo Ошибка сборки. Скопируй текст выше и пришли его в чат.
  pause
  exit /b 1
)
echo.
echo Готово! Файл: publish\DreamIsland.exe
rem Версия, которая не требует установленного .NET (файл около 70 МБ):
rem dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
explorer publish
pause
