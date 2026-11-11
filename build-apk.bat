@echo off
chcp 65001 >nul
setlocal

rem Release-сборка FaceAge, подписанная ключом SataKeyStore.
rem Пароли не хранятся в проекте: скрипт спрашивает их при запуске или берёт из уже заданных переменных
rem FACEAGE_KEY_ALIAS, FACEAGE_STORE_PASS, FACEAGE_KEY_PASS.
rem Файл обязан иметь окончания строк CRLF (см. .gitattributes): с LF cmd режет строки с кириллицей.

if "%FACEAGE_KEY_ALIAS%"=="" set /p "FACEAGE_KEY_ALIAS=Псевдоним ключа в SataKeyStore: "
if "%FACEAGE_STORE_PASS%"=="" set /p "FACEAGE_STORE_PASS=Пароль хранилища: "
if "%FACEAGE_KEY_PASS%"=="" set "FACEAGE_KEY_PASS=%FACEAGE_STORE_PASS%"

if "%FACEAGE_STORE_PASS%"=="" (
    echo Пароль не задан - сборка остановлена.
    exit /b 1
)

rem ILLink 11.0.0 с Android-шагами (MAUI + CameraX + ONNX Runtime) исчерпывает стек: падает то переполнением
rem стека, то access violation, то IL1012. Стек потоков рантайма 64 МБ (значение в hex) это снимает.
set "DOTNET_DefaultStackSize=4000000"

rem RestoreIgnoreFailedSources: если nuget.org недоступен, restore берёт пакеты из локального кэша.
dotnet publish "%~dp0src\FaceAge\FaceAge.csproj" -c Release -f net11.0-android -o "%~dp0dist" -p:RestoreIgnoreFailedSources=true
if errorlevel 1 (
    echo Сборка завершилась с ошибкой.
    exit /b 1
)

echo.
echo Готово: %~dp0dist\com.sata.faceage-Signed.apk
endlocal
