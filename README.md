# FaceAge

Android-приложение на .NET MAUI (.NET 11): оценивает возраст по лицу с фронтальной камеры. Всё считается на телефоне, без сети.

- **Камера** — CameraX: превью и поток кадров RGBA, анализируется только последний кадр.
- **Лицо** — UltraFace RFB-320 (ONNX), **возраст** — InsightFace genderage (ONNX), ONNX Runtime на CPU.
- **Дизайн** — токены «Chrono Business - Apple Design»: карточка `glass-opaque`, Inter, акцент Material You на Android 12+.

## Подготовка

Модель возраста и тестовые фото не хранятся в репозитории по условиям их лицензий — скачайте их один раз:

```powershell
./fetch-assets.ps1
```

## Сборка

```bash
dotnet build src/FaceAge/FaceAge.csproj -c Debug -t:Install   # Debug на эмулятор или телефон
build-apk.bat                                                 # Release APK (arm64), подписанный SataKeyStore
```

`build-apk.bat` спрашивает псевдоним ключа и пароль (или берёт `FACEAGE_KEY_ALIAS`, `FACEAGE_STORE_PASS`, `FACEAGE_KEY_PASS`). Готовый файл — `dist/com.sata.faceage-Signed.apk`.

## Тесты

```bash
dotnet test --project tests/FaceAge.Tests/FaceAge.Tests.csproj
```

Тесты прогоняют `AgeEstimator` из приложения на двух фото во всех поворотах кадра камеры.

## Лицензии сторонних файлов

См. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Модель InsightFace разрешена только для некоммерческого использования: для коммерческого выпуска её нужно заменить.
