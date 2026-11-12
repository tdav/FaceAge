# Экспорт лиц из Oracle и обучение модели возраста — план реализации

> **Исполнителям-агентам:** ОБЯЗАТЕЛЬНЫЙ СКИЛЛ — `superpowers:subagent-driven-development` (рекомендуется) или `superpowers:executing-plans`. Шаги отмечаются флажками `- [ ]`. Дополнительно действует `plan-driven-agents`: нашёл дефект — чинится и код, и этот план.

**Цель:** две программы — `FaceExport` (Windows Forms, .NET 10) выгружает лица из Oracle в папку, `train.py` (Python, PyTorch) обучает по этой папке модель возраста и пишет `.onnx` для FaceAge.

**Архитектура:** программы связывает только формат папки экспорта (описание, раздел 4). Вырезание лица живёт в библиотеке `FaceExport.Core`, которая подключает `AgeEstimator.cs` приложения ссылкой — кроп считается тем же кодом, что в телефоне. Ту же библиотеку использует `tools/AgeEval`, чтобы прогнать всю цепочку на UTKFace без Oracle и без видеокарты.

**Стек:** .NET 10 (`net10.0`, `net10.0-windows`), Windows Forms, `Oracle.ManagedDataAccess.Core`, ONNX Runtime 1.31.1; Python 3.13, PyTorch 2.14.1, onnx2torch, timm.

**Описание проекта:** `docs/superpowers/specs/2026-11-12-age-training-design.md` — исполнитель читает его вместе с планом.

## Общие ограничения

- Комментарии в коде, сообщения коммитов, тексты окна и консоли — на русском. Идентификаторы — на английском.
- C#: приватные поля без префикса `_`, обращение через `this.`; private-методы-помощники не создавать — логика публичного метода пишется в его теле.
- **Тесты не пишутся и не изменяются** (нет подтверждения заказчика): ни тестовый код, ни тестовые данные, ни моки. Проверка — запуском программ и контрольными экспериментами из шагов задач. Существующие тесты `tests/FaceAge.Tests` только запускаются.
- Сборка .NET: всегда с `-p:RestoreIgnoreFailedSources=true` (nuget.org недоступен, пакеты берутся из локального кэша). Новые версии пакетов не подбирать — только перечисленные в плане.
- Git: индексировать только точными путями файлов; коммит в форме `git commit -m "..." -- <точные пути>`; `git add -A`, `git add .` и папки запрещены. Не пушить, в `main` не сливать. Сообщение коммита заканчивается строкой `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`.
- Процессы: запоминать PID своего процесса и снимать только его; `taskkill /IM` запрещён. При блокировке вывода сборки — собирать в отдельный каталог через `-o` (путь в кавычках, после сборки `git status`).
- Персональные данные и тяжёлые файлы в репозиторий не попадают: данные, окружение Python и результаты обучения лежат в `E:\Works_Sata\FaceAge-data\`.
- Пароль Oracle: только в памяти процесса; не в настройках, не в журнале, не в тексте исключений, показываемых пользователю.
- Константы договора (не менять без правки описания): размер 256, `cropScale` 1.75, `modelCropScale` 1.5, JPEG 95, заполнение за краем `replicate`, отложенная часть 15 %, целевые возрасты 13–50, чтение 10–55.

## Проверенные факты (2026-11-12)

Всё ниже проверено запуском, а не по памяти. Исходники проверок — `docs/superpowers/plans/2026-11-12-age-training-spikes/`.

| Что | Результат |
|---|---|
| Автономная сборка WinForms `net10.0-windows`, `win-x64` из кэша | работает с `<RuntimeFrameworkVersion>10.0.10</RuntimeFrameworkVersion>`; с 10.0.14 — ошибка NU1101 (нет пакета хоста). 228 файлов, 140 МБ |
| Пакеты в кэше | `Oracle.ManagedDataAccess.Core` 23.26.400 (есть `net8.0`), `Microsoft.ML.OnnxRuntime` 1.31.1, `Microsoft.Maui.Graphics` 10.0.110, `Microsoft.Data.Sqlite` 10.0.7 |
| `AgeEstimator.cs` ссылкой в библиотеке `net10.0` с `<Using Include="Microsoft.Maui.Graphics" />` | собирается без предупреждений. В проекте WinForms так делать нельзя: глобальный `System.Drawing` конфликтует с `SizeF` |
| Члены драйвера Oracle (рефлексией по сборке) | `OracleCommand.BindByName`, `.CommandTimeout`, `.Cancel()`; `OracleDataReader.Read()`, `.IsDBNull(int)`, `.GetDateTime(int)`, `.GetOracleBlob(int)`; `OracleBlob.Value` (`byte[]`); `OracleConnectionStringBuilder.DataSource`, `.UserID`, `.Password` |
| Зависимости `onnxruntime.dll` | `MSVCP140.dll`, `MSVCP140_1.dll`, `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll` и UCRT. В автономную сборку они не попадают — их нужно класть рядом с программой |
| .NET 10 и Windows | поддерживаются Windows Server 2012 и новее (x64), Windows 10 1607+ |
| `genderage.onnx` | opset 12; вход `data` [N,3,96,96]; выход `fc1`; 31 Conv + 31 BatchNorm + 2 головы (пол, возраст); 325 тыс. параметров |
| `onnx2torch` 1.5.15: ONNX → PyTorch | выходы совпадают с ONNX Runtime до 1.7e-6; обучение идёт; при потере только по возрасту ветка пола градиента не получает |
| `torch.onnx.export(..., dynamo=False, opset_version=17)` | обе модели экспортируются, совпадение с PyTorch до 3e-5, пакет любого размера, ONNX Runtime 1.31.1 загружает |
| Основа `large` | `timm` `mobilenetv3_large_100.ra_in1k`: лицензия весов apache-2.0, 4,3 млн параметров, вход 224, mean (0.485, 0.456, 0.406), std (0.229, 0.224, 0.225) |
| PyTorch под NVIDIA | на 2026-11-12 сборки CUDA для Windows есть для 2.14.1 (`cu126`, `cu130`), для 2.15.0 их нет. Поэтому версия — **2.14.1** |
| Версии Python-пакетов (обе проверки пройдены на них) | `torch==2.14.1`, `torchvision==0.29.2`, `onnx==1.23.2`, `onnx2torch==1.5.15`, `onnxruntime==1.31.1`, `timm==1.0.31`, `pillow==13.0.0`, `numpy==2.5.4` |

**Не проверено и проверить негде:** чтение из настоящей базы Oracle (доступной базы нет). Цикл чтения проверяется на SQLite через общий ADO.NET (задача A2); особенности драйвера Oracle впервые проверяются на сервере заказчика.

## На что смотреть при ревью

Пять ситуаций, которые описание не разбирает, но человек с ними встретится. У каждой есть контрольный шаг в задаче-владельце.

1. **«Стоп» во время долгого запроса** (запрос просматривает таблицу минутами, строк ещё нет). Ожидание: запрос отменяется, окно не висит до конца запроса. → A2, A3.
2. **SQL вернул не те поля** (другой порядок, дата строкой, фото не BLOB). Ожидание: понятное сообщение с номером поля, а не необработанное исключение. → A2.
3. **Массовый пропуск фото** (повёрнутые снимки, другой формат): пара «завершена», а сохранено почти ничего. Ожидание: предупреждение в журнале, когда пропущено больше 10 % пары. → A2.
4. **Повторный «Старт»** на готовой или частично готовой папке, в том числе с другим диапазоном возрастов. Ожидание: ничего не дублируется, готовые пары пропускаются. → A1, A2.
5. **В отложенной части нет фото 13–50 лет** (первый экспорт на одном возрасте, малый набор). Ожидание: `train.py` говорит об этом прямо, а не падает на делении на ноль и не пишет `nan` в отчёт. → B2, B3.

## Окружение исполнителя

- Windows 11, PowerShell 7 (основная оболочка) и Git Bash. Репозиторий — `E:\Works_Sata\FaceAge`, ветка `main`, удалённый `tdav/FaceAge` (публичный).
- .NET SDK 11.0.100 (собирает и `net10.0`), SDK 10.0.302. Docker не нужен. Базы данных нет.
- Данные вне репозитория: `E:\Works_Sata\FaceAge-data\utk\{img,img1,img2}` (UTKFace, файлы `<номер>_<возраст>.jpg`, 200×200), `...\export-utk` (папка экспорта для пробного прогона), `...\venv` (Python), `...\runs` (результаты обучения).
- Python: только `E:\Works_Sata\FaceAge-data\venv\Scripts\python.exe`. В системный Python ничего не ставить. Видеокарты нет — всё на процессоре. Ключ `-I` при запуске скриптов из `tools/train` **не ставить**: в изолированном режиме каталог скрипта не попадает в `sys.path`, и `train.py` не найдёт `models.py` и `dataset.py`. Однострочные проверки запускать из каталога `tools/train`.
- `src/FaceAge/Resources/Raw/genderage.onnx` не хранится в git: в новом рабочем дереве его нет, оркестратор копирует его туда в задаче 0.
- Сборка Android-приложения в этом плане не нужна и не запускается.

## Скиллы, агенты, волны

- **Скиллы:** `superpowers:using-git-worktrees` (два рабочих дерева), `superpowers:subagent-driven-development`, `superpowers:dispatching-parallel-agents`, `plan-driven-agents`, `fullstack-dev-skills:csharp-developer` и `dotnet-claude-kit:modern-csharp` (ветка A), `fullstack-dev-skills:python-pro` и `fullstack-dev-skills:ml-pipeline` (ветка B), `dotnet-test:run-tests` (только прогон существующих тестов), `superpowers:verification-before-completion`, `code-review`, `merge-worktrees`, `superpowers:finishing-a-development-branch`. Скилл `ora` не используется: доступной базы нет.
- **Агенты:** исполнители задач — `general-purpose`, модель `sonnet`; сломанная сборка — `dotnet-claude-kit:build-error-resolver`; ревью ветки A — `dotnet-claude-kit:code-reviewer`, модель `opus`; ревью ветки B — `general-purpose`, модель `opus`; перед слиянием — `merge-reviewer`.
- **Параллельность:** до 5 агентов одновременно; в этом плане независимых задач в волне две, потому что внутри ветки задачи зависят друг от друга.

| Волна | Ветка A `feature/face-export` | Ветка B `feature/age-training` |
|---|---|---|
| 0 | задача 0 (оркестратор) | |
| 1 | A1 | B1 |
| 2 | A2 | B2 (нужна папка `export-utk` из A1) |
| 3 | A3 | B3 (нужен собранный `AgeEval` из A1) |
| 4 | ревью A | ревью B, затем `merge-reviewer` |

- **Прогресс:** чек-лист — `.claude/process.md`. Журнал выводов — `.superpowers/sdd/2026-11-12-age-training/progress.md` (рабочая папка плана, в git не попадает; там же брифы задач, отчёты и пакеты для ревью); его ведёт только оркестратор и записывает туда всё, что меняет следующие задачи (уточнённые сигнатуры, пути рабочих деревьев, измеренные числа).

## Структура файлов

```
tools/FaceExport.Core/            библиотека net10.0 — ветка A
  FaceExport.Core.csproj
  FaceCropper.cs                  лицо → кроп 256×256 RGB и оценка нынешней модели
  ExportFolder.cs                 папка экспорта: export.json, metadata.jsonl, progress.jsonl, возобновление
  ExportRun.cs                    цикл «возраст × пол → запрос → строки» поверх ADO.NET
tools/FaceExport/                 приложение WinForms net10.0-windows — ветка A
  FaceExport.csproj
  Program.cs
  MainForm.cs                     окно, настройки, запуск и остановка
  GdiCodec.cs                     открытие фото и запись JPEG через System.Drawing
  README.md
tools/AgeEval/                    изменяется — ветка A
  AgeEval.csproj                  ссылка на FaceExport.Core вместо своей ссылки на AgeEstimator.cs
  Program.cs                      режимы export, make-db, export-db; переменная FACEAGE_AGE_MODEL
tools/train/                      ветка B
  requirements.txt
  models.py                       две модели, потери, экспорт в ONNX
  dataset.py                      чтение папки экспорта, деление, «вид телефона», искажения
  train.py                        обучение, оценка, отчёт, сверка pred
  README.md
.gitignore                        изменяется только в ветке B
```

---

### Задача 0: подготовка (оркестратор, до волны 1)

- [ ] **Шаг 1. Зелёный `main`.**
  Запуск: `dotnet build tools/AgeEval/AgeEval.csproj -c Release -p:RestoreIgnoreFailedSources=true` → `0 Error(s)`.
  Запуск: `dotnet build tests/FaceAge.Tests/FaceAge.Tests.csproj -p:RestoreIgnoreFailedSources=true`, затем `dotnet test --project tests/FaceAge.Tests/FaceAge.Tests.csproj --no-build` → 16 пройдено, 0 упало. Так тесты запускаются везде в этом плане: лишние параметры после `dotnet test` уходят в само тестовое приложение и ломают прогон.
  Если красное — чинится отдельным коммитом до волны 1, а не обходами в задачах.
- [ ] **Шаг 2.** Записать `.claude/process.md`: заголовок плана и задачи 0, A1–A3, B1–B3, ревью.
- [ ] **Шаг 3.** Скопировать UTKFace в `E:\Works_Sata\FaceAge-data\utk` из `C:\Users\admin\AppData\Local\Temp\claude\5b45dfeddacbef2e\4a323eca-2193-4b01-bc4b-6253bb960fbb\scratchpad\utk` (папки `img`, `img1`, `img2`). Ожидание: 23 705 файлов `.jpg` (7 902 + 7 902 + 7 901).
- [ ] **Шаг 4.** Создать окружение: `python -m venv E:\Works_Sata\FaceAge-data\venv`, затем `pip install` с версиями из таблицы фактов. Ожидание: `pip list` показывает ровно эти версии.
- [ ] **Шаг 5.** Создать два рабочих дерева скиллом `superpowers:using-git-worktrees`: ветки `feature/face-export` и `feature/age-training` от `main`. В оба скопировать `src/FaceAge/Resources/Raw/genderage.onnx`, а в дерево ветки A — ещё и `tests/FaceAge.Tests/Photos/*.jpg` (эти файлы не хранятся в git, без них существующие тесты не запускаются). Пути деревьев записать в журнал.
- [ ] **Шаг 6.** Создать журнал `.superpowers/sdd/2026-11-12-age-training/progress.md` с путями и результатами шагов 1–5.

Описание, план и проверки лежат в основном каталоге `E:\Works_Sata\FaceAge\docs\superpowers\` незакоммиченными; агенты читают их по абсолютному пути. Куда их коммитить, решает заказчик в конце.

---

## Ветка A — `feature/face-export`

### Задача A1: библиотека вырезания лица и папка экспорта; экспорт UTKFace

**Файлы:**
- Создать: `tools/FaceExport.Core/FaceExport.Core.csproj`, `tools/FaceExport.Core/FaceCropper.cs`, `tools/FaceExport.Core/ExportFolder.cs`
- Изменить: `tools/AgeEval/AgeEval.csproj`, `tools/AgeEval/Program.cs`

**Интерфейсы:**
- Использует: `FaceAge.Vision.AgeEstimator` (`src/FaceAge/Vision/AgeEstimator.cs`, менять нельзя): `new AgeEstimator(byte[] detectorModel, byte[] ageModel) { Calibrate = false }`, `AgeReading Analyze(ReadOnlySpan<byte> rgba, int width, int height, int rowStride, int rotation)`; у `AgeReading` — `HasFace`, `Face` (`RectF` в пикселях кадра), `Roll` (градусы), `Age`.
- Создаёт (пространство имён `FaceAge.Export`):

```csharp
public readonly record struct CropResult(bool Found, byte[] Rgb, float Pred);

public sealed class FaceCropper : IDisposable
{
    public const int Size = 256;
    public const float CropScale = 1.75f;
    public const float ModelCropScale = 1.5f;
    public FaceCropper(byte[] detectorModel, byte[] ageModel);
    // rgba — пиксели R,G,B,A построчно с шагом rowStride. Rgb в ответе — Size×Size×3, R,G,B.
    public CropResult Cut(ReadOnlySpan<byte> rgba, int width, int height, int rowStride);
}

public sealed class ExportFolder : IDisposable
{
    public ExportFolder(string path, string detector);        // detector — имя файла модели, например "yunet_320.onnx"
    public IReadOnlySet<(int Age, string Sex)> Completed { get; }
    public int Dropped { get; }                                // строк незавершённой пары удалено при открытии
    public void Add(byte[] jpeg, DateOnly birth, DateOnly shot, string sex, float pred);
    public void CompletePair(int age, string sex, int returned, int saved, int skipped);
}
```

- [ ] **Шаг 1. `FaceExport.Core.csproj`.** Взять за основу `docs/superpowers/plans/2026-11-12-age-training-spikes/SpikeCore.csproj.txt` (проверен сборкой): `net10.0`, пакеты `Microsoft.ML.OnnxRuntime` 1.31.1 и `Microsoft.Maui.Graphics` 10.0.110, `<Using Include="Microsoft.Maui.Graphics" />`, ссылка `<Compile Include="..\..\src\FaceAge\Vision\AgeEstimator.cs" Link="Linked\AgeEstimator.cs" />`.

- [ ] **Шаг 2. `FaceCropper.Cut`.** Вызывает `Analyze(rgba, width, height, rowStride, 0)`; без лица возвращает `new CropResult(false, [], 0)`; иначе `Pred = reading.Age`. Геометрия — как в `AgeEstimator.Analyze`, с двумя отличиями из раздела 4 описания:
  - центр — центр `reading.Face`; сторона квадрата = `max(Face.Width, Face.Height) × CropScale`; угол = `reading.Roll × π / 180`; шаг `step = сторона / Size` (пикселей фото на пиксель кропа);
  - точка фото для смещения `(ox, oy)` от центра кропа: `u = cx + ox·cos − oy·sin`, `v = cy + ox·sin + oy·cos`;
  - усреднение: каждый пиксель кропа — среднее `n × n` выборок, `n = max(1, ceil(step))`, выборки равномерно внутри площадки пикселя; каждая выборка — билинейная;
  - за краем фото координаты прижимаются к `[0, width−1]` и `[0, height−1]` (продолжение крайних пикселей), чёрного цвета нет.

  Набросок внутреннего цикла — **не прогонялся, правило выше главнее**:

```csharp
for (var j = 0; j < n; j++)
for (var i = 0; i < n; i++)
{
    var ox = (a + (i + 0.5f) / n - Size / 2f) * step;
    var oy = (b + (j + 0.5f) / n - Size / 2f) * step;
    var u = Math.Clamp(cx + ox * cos - oy * sin - 0.5f, 0f, width - 1);
    var v = Math.Clamp(cy + ox * sin + oy * cos - 0.5f, 0f, height - 1);
    // билинейная выборка четырёх соседей (u0, v0)…(u0+1, v0+1), индексы тоже прижаты к краю
}
```

- [ ] **Шаг 3. `ExportFolder`.** Формат файлов — раздел 4 описания, буквально. Поведение:
  - Конструктор создаёт папку и `images\`. Если `export.json` нет — пишет его: `{"version":1,"size":256,"cropScale":1.75,"modelCropScale":1.5,"border":"replicate","jpegQuality":95,"detector":"…"}`. Если есть и любое из полей `version`, `size`, `cropScale`, `modelCropScale`, `border` отличается — `InvalidOperationException` с текстом «Папка создана с другими параметрами экспорта: <поле> = <значение>, ожидалось <значение>».
  - Конструктор приводит `progress.jsonl` к виду «только полные строки, каждая оканчивается `\n`»: строка без завершающего `\n` отбрасывается и вычищается из файла, даже если разбирается как JSON. Иначе следующая запись приклеится к обрывку, сумма `saved` окажется заниженной, и при следующем открытии молча пропадут фото завершённой пары (найдено ревью A1).
  - Конструктор читает `progress.jsonl`: `Completed` — пары из него, `N` — сумма `saved`. В `metadata.jsonl` остаются первые `N` строк; для каждой лишней строки удаляется её файл в `images\`, файл описания переписывается, `Dropped` = число лишних строк. Оборванная последняя строка считается лишней. Это работает, потому что пары идут строго одна за другой и файл только дописывается.
  - `Add`: полных лет = `shot.Year − birth.Year`, минус 1, если день рождения в году съёмки ещё не наступил. Пишет `images\<полных лет>\<guid>.jpg`, затем строку `{"file":"<полных лет>/<guid>.jpg","birth":"ГГГГ-ММ-ДД","shot":"ГГГГ-ММ-ДД","sex":"…","pred":31.4}` и сбрасывает её на диск (`Flush(true)`). `pred` — с одним знаком после точки, инвариантная культура.
  - `CompletePair` дописывает `{"age":27,"sex":"F","returned":2007,"saved":2000,"skipped":7}`, сбрасывает на диск, добавляет пару в `Completed`.
  - Кодировка — UTF-8 без BOM, перевод строки `\n`.

- [ ] **Шаг 4. `AgeEval.csproj`.** Убрать строку `<Compile Include="..\..\src\FaceAge\Vision\AgeEstimator.cs" …/>`, добавить `<ProjectReference Include="..\FaceExport.Core\FaceExport.Core.csproj" />`. Остальное не менять.

- [ ] **Шаг 5. `Program.cs`: модель из переменной окружения и режим `export`.**
  - Путь к модели возраста: `Environment.GetEnvironmentVariable("FACEAGE_AGE_MODEL") ?? Path.Combine(models, "genderage.onnx")` — во всех режимах.
  - Режим: `AgeEval export <папки через ;> <папка экспорта> [лимит] [холст]`. `холст` по умолчанию 400 (серое поле, как в существующем замере); `0` — фото подаётся как есть, без поля.
  - Файлы группируются по возрасту из имени (`<номер>_<возраст>.jpg`), группы идут по возрастанию возраста; группа, уже записанная в `Completed` с полом `"U"`, пропускается.
  - Для каждого файла: открыть (ImageSharp) → RGBA → `FaceCropper.Cut` → при `Found` закодировать JPEG с качеством 95 (ImageSharp) → `ExportFolder.Add` с условными датами `shot = 2020-07-01`, `birth = shot.AddYears(-возраст).AddDays(-182)`, пол `"U"`. После группы — `CompletePair(возраст, "U", …)`.
  - В конце печатает: сколько сохранено, сколько пропущено, `Dropped`.
  - Существующий режим замера (первый аргумент — не имя режима) работает как раньше.

- [ ] **Шаг 6. Сборка.** `dotnet build tools/AgeEval/AgeEval.csproj -c Release -p:RestoreIgnoreFailedSources=true` → `0 Error(s)`; предупреждения — только `NU1902` и `NU1903` (сведения об уязвимостях пакетов; они есть и на `main`), новых быть не должно.

- [ ] **Шаг 7. Проверка формы.** Запуск: `dotnet run --project tools/AgeEval -c Release --no-build -- export "E:\Works_Sata\FaceAge-data\utk\img1" "E:\Works_Sata\FaceAge-data\export-check" 300`.
  Ожидание: число строк `metadata.jsonl` = число `.jpg` в `images\` = сумме `saved` в `progress.jsonl`; каждый JPEG 256×256; `export.json` как в шаге 3. Открыть глазами три кропа (инструмент Read показывает изображения): лицо по центру, глаза горизонтальны, лицо занимает чуть больше половины ширины кадра.

- [ ] **Шаг 8. Контрольный эксперимент: края фото.** Скопировать `tests/FaceAge.Tests/Photos/*.jpg` (паспортные фото, кадрированы тесно, как ICAO) во временную папку под именами `00001_34.jpg` и `00002_29.jpg` и экспортировать с холстом `0`. Ожидание: в кропах нет чёрных полос; за краем фото продолжается фон. Если фото в рабочем дереве нет (они не хранятся в git) — взять из основного каталога `E:\Works_Sata\FaceAge\tests\FaceAge.Tests\Photos`.

- [ ] **Шаг 9. Контрольный эксперимент: обрыв и возобновление** (ситуация 4). Запустить экспорт 2 000 файлов в новую папку, через 5 секунд снять процесс по его PID, запустить ту же команду снова. Ожидание: второй запуск печатает `Dropped` больше нуля либо ноль, если обрыв пришёлся на границу пары; в итоге в `metadata.jsonl` нет повторяющихся `file`, все файлы из строк существуют, число строк = сумме `saved`. Третий запуск той же команды ничего не добавляет.

- [ ] **Шаг 10. Контрольный эксперимент: чужая папка.** В `export.json` заменить `1.75` на `1.5`, запустить экспорт. Ожидание: отказ с сообщением из шага 3, файлы не тронуты. Вернуть значение.

- [ ] **Шаг 11. Экспорт всего UTKFace** в `E:\Works_Sata\FaceAge-data\export-utk` (три папки через `;`, без лимита, в фоне — это минуты). В отчёте назвать: сохранено, пропущено, время на фото. Папки `export-check` и временные удалить.

- [ ] **Шаг 12. Прогон существующих тестов** (команды — как в задаче 0, шаг 1) → 16 пройдено. Затем коммит точными путями пяти файлов из блока «Файлы»: «FaceExport.Core: вырезание лица и папка экспорта; режим export в AgeEval».

Ограничение, о котором нужно помнить: в UTKFace лицо занимает весь кадр 200×200, поэтому кропы на сером поле захватывают само поле. Для проверки цепочки этого достаточно; качество кропа на настоящих фото проверяет только шаг 8 и первый запуск у заказчика.

### Задача A2: цикл чтения из базы; проверка на SQLite

**Файлы:**
- Создать: `tools/FaceExport.Core/ExportRun.cs`
- Изменить: `tools/AgeEval/AgeEval.csproj` (пакет `Microsoft.Data.Sqlite` 10.0.7), `tools/AgeEval/Program.cs`

**Интерфейсы:**
- Использует: `FaceCropper`, `ExportFolder`, `CropResult` из A1.
- Создаёт:

```csharp
public readonly record struct ExportProgress(
    int Age, string Sex, int PairIndex, int PairCount,
    int Returned, int Saved, int Skipped, int TotalSaved, int TotalSkipped,
    string? Line);                       // Line — готовая строка журнала, когда есть что сказать

public delegate bool PhotoDecoder(byte[] photo, out byte[] rgba, out int width, out int height);

public sealed class ExportRun
{
    public ExportRun(FaceCropper cropper, ExportFolder folder, PhotoDecoder decode, Func<byte[], byte[]> encodeJpeg);
    // encodeJpeg: RGB 256×256×3 → JPEG с качеством 95.
    public void Run(DbConnection connection, string sql, int ageFrom, int ageTo, IReadOnlyList<string> sexes,
        Action<DbCommand>? configure, IProgress<ExportProgress> progress, CancellationToken token);
}
```

- [ ] **Шаг 1. `ExportRun.Run`.** Соединение приходит открытым. Пары — возрасты по возрастанию, внутри возраста значения пола в заданном порядке. На пару — одна команда: `CommandText = sql`, `CommandTimeout = 0`, параметры `age` (целое) и `sex` (строка) через `command.CreateParameter()` с именами **без двоеточия**, затем `configure?.Invoke(command)`. Поля читаются по позиции: 0 — фото, 1 — дата рождения, 2 — дата съёмки. Фото: `reader.GetValue(0)`, годится только `byte[]`; любой другой тип — сообщение из таблицы ниже (что делать с Oracle, если он вернёт не `byte[]`, — A3, шаг 2). Строки обрабатываются по одной.

  Все пути — ни один не возвращает управление молча:

| Ситуация | Что делает `Run` |
|---|---|
| пара уже в `folder.Completed` | пропускает; `Line` = «возраст 27, F: уже выгружено» |
| поле пустое (`IsDBNull`) или съёмка раньше рождения | строка пропущена, причина «даты или фото пустые» |
| `decode` вернул `false` | пропущена, причина «не открылось» |
| `Cut` не нашёл лицо | пропущена, причина «лицо не найдено» |
| поле не читается как `byte[]` / `DateTime` | `InvalidOperationException`: «Поле 2 должно быть датой рождения (DATE), а пришло <тип>» — с номером поля от 1 |
| токен отменён (до первой строки или между строками) | `command.Cancel()`, `OperationCanceledException`; `CompletePair` **не** вызывается |
| токен отменён, а драйвер тихо закончил выборку (`Read()` вернул `false`) | проверка токена после цикла чтения, до `CompletePair`: `OperationCanceledException`, пара **не** завершена (найдено ревью A2) |
| пар нет (`ageFrom > ageTo` или пустой список пола) | `ArgumentException` «Нет пар для выгрузки: проверьте диапазон возрастов и значения пола» |
| ошибка базы или диска | исключение уходит наверх; `CompletePair` **не** вызывается |
| запрос вернул 0 строк | `CompletePair` с нулями; `Line` = «возраст 12, M: в базе нет фото» |
| пара закончилась | `CompletePair`; `Line` = «возраст 26, M: сохранено 2 000, пропущено 4 (лицо не найдено 3, не открылось 1)» |
| пропущено больше 10 % пары | к `Line` добавляется «— ВНИМАНИЕ: пропущена большая доля фото» |
| все пары были завершены ещё до запуска | последняя `Line` = «Все пары уже выгружены» |

  Отмена во время выполнения запроса: `using var registration = token.Register(command.Cancel);` — иначе «Стоп» ждёт конца запроса (ситуация 1). `progress.Report` — после каждой строки.

- [ ] **Шаг 2. Режим `make-db` в AgeEval:** `AgeEval make-db <папки через ;> <файл.db> [лимит]`. Создаёт SQLite с таблицей `persons(photo BLOB, birth_date TEXT, shoot_date TEXT, sex TEXT, age INTEGER)`: фото — байты файла UTKFace как есть, даты — как в A1 шаг 5 (формат `yyyy-MM-dd HH:mm:ss`), пол — `M` для чётного номера файла, `F` для нечётного. Дополнительно четыре «плохие» строки с `age = 26`, `sex = 'M'`: пустое фото (`NULL`), мусорные байты вместо фото, съёмка раньше рождения, серый квадрат без лица.

- [ ] **Шаг 3. Режим `export-db` в AgeEval:** `AgeEval export-db <файл.db> <папка экспорта> <от> <до> <значения пола через ;>`. Открывает SQLite, вызывает `ExportRun.Run` с запросом `SELECT photo, birth_date, shoot_date FROM persons WHERE sex = :sex AND age = :age`, `configure = null`, кодеками на ImageSharp; печатает каждую `Line`. `Ctrl+C` отменяет токен.

- [ ] **Шаг 4. Сборка** — как A1 шаг 6: без ошибок и без новых предупреждений.

- [ ] **Шаг 5. Проверка смысла.** `make-db` на `img1` с лимитом 3 000, затем `export-db … 24 28 "M;F"`.
  Ожидание: 10 строк журнала по парам (для возрастов, которых в выборке нет, — «в базе нет фото»); для пары 26/M пропущено ровно 4: «даты или фото пустые» — 2, «не открылось» — 1, «лицо не найдено» — 1; число строк `metadata.jsonl` = сумме «сохранено»; в строках `sex` равен `M` или `F`; подпапки `images\` — только возрасты из 24…28.
  Если SQLite не привязал параметры по именам без двоеточия — записать это в отчёт и дать SQLite имена с двоеточием **только в режиме `export-db`** через `configure`; в `ExportRun` имена остаются без двоеточия (так требует Oracle).

- [ ] **Шаг 6. Контрольные эксперименты.**
  - Повторный запуск той же команды: ничего не добавлено, последняя строка «Все пары уже выгружены» (ситуация 4). Запуск с диапазоном `22 30`: добавлены только новые пары.
  - Не те поля (ситуация 2): временно поменять местами `photo` и `birth_date` в запросе режима → сообщение «Поле 1 должно быть фото…», код возврата не 0. Вернуть.
  - Массовый пропуск (ситуация 3): временно заставить кодек `decode` возвращать `false` для каждого второго фото → в строках журнала есть «ВНИМАНИЕ». Вернуть.
  - Отмена (ситуация 1, в пределах возможного без Oracle): `Ctrl+C` посреди пары → код возврата 130 или сообщение «Остановлено»; пара не появилась в `progress.jsonl`; повторный запуск её доделывает.
  - После экспериментов `git diff` показывает только задуманные изменения.

- [ ] **Шаг 7. Коммит** точными путями трёх файлов: «FaceExport.Core: цикл чтения из базы; проверка на SQLite в AgeEval».

### Задача A3: приложение FaceExport (Windows Forms), сборка для сервера, README

**Файлы:**
- Создать: `tools/FaceExport/FaceExport.csproj`, `tools/FaceExport/Program.cs`, `tools/FaceExport/MainForm.cs`, `tools/FaceExport/GdiCodec.cs`, `tools/FaceExport/README.md`

**Интерфейсы:**
- Использует: `FaceCropper`, `ExportFolder`, `ExportRun`, `ExportProgress`, `PhotoDecoder` (A1, A2).
- Создаёт: `public static class GdiCodec { public static bool Decode(byte[] photo, out byte[] rgba, out int width, out int height); public static byte[] EncodeJpeg(byte[] rgb); }` — `Decode` подходит под `PhotoDecoder`.

- [ ] **Шаг 1. `FaceExport.csproj`.** Основа — `…spikes/SpikeExport.csproj.txt` (проверена публикацией и запуском): `WinExe`, `net10.0-windows`, `UseWindowsForms`, `RuntimeIdentifier` `win-x64`, `SelfContained` `true`, **`RuntimeFrameworkVersion` `10.0.10`**, свойство `OracleDriverVersion` со значением по умолчанию `23.26.400` и `<PackageReference Include="Oracle.ManagedDataAccess.Core" Version="$(OracleDriverVersion)" />`. Добавить: `<ProjectReference Include="..\FaceExport.Core\FaceExport.Core.csproj" />`; копирование `..\..\src\FaceAge\Resources\Raw\yunet_320.onnx` и `genderage.onnx` в `Models\`; цель после `Publish`, копирующую `msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` из `$(SystemRoot)\System32` в каталог публикации. Пакеты `Microsoft.Maui.Graphics` и `Microsoft.ML.OnnxRuntime` напрямую не подключать — они приходят из Core; глобальный `Using` для `Microsoft.Maui.Graphics` **не добавлять**.

- [ ] **Шаг 2. `GdiCodec`.** `Decode`: `Bitmap` из потока → 32 бита → байты в порядке R,G,B,A (в памяти GDI+ порядок B,G,R,A — переставить); любое исключение при открытии → `false`. `EncodeJpeg`: `Bitmap` 256×256 24 бита из RGB (в памяти B,G,R) → JPEG через `ImageCodecInfo` с `Encoder.Quality = 95L`.
  Чтение BLOB — в `MainForm`, через `configure`: `command => ((OracleCommand)command).BindByName = true`. **Этот код на базе не запускался.** Если на сервере `reader.GetValue(0)` вернёт не `byte[]`, а `OracleBlob`, сообщение из A2 («Поле 1 должно быть фото… пришло OracleBlob») скажет об этом прямо; исправление — `GetOracleBlob(0).Value`.

- [ ] **Шаг 3. `MainForm`.** Окно строится кодом (без файла дизайнера), раскладка — схема из раздела 5 описания. Заголовок: «FaceExport — экспорт лиц для обучения». Поля: сервер, пользователь, пароль (`UseSystemPasswordChar`), SQL (многострочное, моноширинный шрифт; по умолчанию — образец из описания), возраст от (10) и до (55), значения `:sex` через `;`, папка и «Обзор». Ниже — строка текущей пары, полоса хода, счётчики «Сохранено», «Пропущено», «Скорость», «Осталось», журнал. Кнопки внизу справа: «Стоп», правее «Старт».
  - «Старт» проверяет: сервер, пользователь, пароль, папка не пустые; SQL содержит `:age` и `:sex`; 0 ≤ от ≤ до ≤ 120; есть хотя бы одно значение пола. Ошибка — сообщением с названием поля, работа не начинается.
  - Настройки (всё, кроме пароля) сохраняются по «Старт» в `%AppData%\FaceExport\settings.json` и подставляются при запуске.
  - Модели читаются из `Models\` рядом с программой; если файла нет — сообщение с его именем, работа не начинается.
  - Работа — в одном `Task.Run`: строка подключения из `OracleConnectionStringBuilder { DataSource, UserID, Password }`, `Open()`, `new ExportFolder(папка, "yunet_320.onnx")`, `ExportRun.Run`. На время работы поля и «Старт» выключены, «Стоп» включён.
  - Завершение: успех — «Экспорт завершён: сохранено N, пропущено M»; `OperationCanceledException` — «Остановлено. При следующем запуске незаконченная пара будет выгружена заново»; любое другое исключение — его сообщение. После любого исхода поля снова включены.
  - Закрытие окна во время работы: отменяет токен и ждёт конца текущего фото.
  - Если `ExportFolder.Dropped` больше нуля — строка журнала «Удалено N строк незавершённой пары».

- [ ] **Шаг 4. Сборка и публикация.** `dotnet publish tools/FaceExport/FaceExport.csproj -c Release -o dist/FaceExport -p:RestoreIgnoreFailedSources=true` → код 0. В `dist\FaceExport` есть `FaceExport.exe`, `Models\yunet_320.onnx`, `Models\genderage.onnx`, `onnxruntime.dll` и четыре библиотеки VC++. `git status` чист (`dist/` в `.gitignore`).

- [ ] **Шаг 5. Проверка запуском.** Запустить `dist\FaceExport\FaceExport.exe`, запомнить PID. Ожидание: в течение 15 секунд у процесса появляется окно с заголовком из шага 3. Сделать снимок окна (PowerShell: `Graphics.CopyFromScreen` по прямоугольнику окна процесса) и посмотреть его: раскладка соответствует схеме, кнопки справа внизу, «Старт» крайняя справа. Если снимок сделать не удалось — так и написать в отчёте, а не считать шаг пройденным.

- [ ] **Шаг 6. Контрольные эксперименты (без базы).**
  - «Старт» с пустым паролем → сообщение с названием поля.
  - Сервер `127.0.0.1:1/none` → через разумное время сообщение об ошибке подключения; поля снова включены; в папке экспорта нет `progress.jsonl`.
  - После этого прочитать `%AppData%\FaceExport\settings.json`: пароля в нём нет ни под каким именем. Поискать введённый пароль по всей папке `%AppData%\FaceExport` и по папке экспорта — совпадений нет.
  - Снять процесс по PID.

- [ ] **Шаг 7. `README.md`** (русский): что делает программа; сборка (команда шага 4) и что копировать на сервер; требования (Windows Server 2016 и новее рекомендуется; .NET ставить не нужно); порядок первого запуска — один возраст (от = до), просмотр нескольких лиц в `images\`, только потом весь диапазон; формат папки одной ссылкой на описание; как пересобрать для старой базы (`-p:OracleDriverVersion=3.21.120`); что делать при сообщениях «Поле N должно быть…» и «ВНИМАНИЕ: пропущена большая доля фото»; раздел «Не проверено на настоящей базе»: чтение BLOB, отмена долгого запроса, версия драйвера.

- [ ] **Шаг 8. Коммит** точными путями пяти файлов: «FaceExport: окно экспорта лиц из Oracle».

---

## Ветка B — `feature/age-training`

### Задача B1: модели, потери, экспорт в ONNX

**Файлы:**
- Создать: `tools/train/requirements.txt`, `tools/train/models.py`
- Изменить: `.gitignore` (добавить `__pycache__/`, `tools/train/runs/`, `*.pt`)

**Интерфейсы — создаёт:**

```python
SMALL_INPUT = 96
LARGE_INPUT = 224
LARGE_BACKBONE = "mobilenetv3_large_100.ra_in1k"

def build_small(onnx_path: str) -> torch.nn.Module: ...      # выход [N,3]: пол, пол, возраст/100
def build_large(pretrained: bool) -> torch.nn.Module: ...    # forward(data) -> [N] лет; logits(data) -> [N,101]
def predict_age(model, kind: str, data: torch.Tensor) -> torch.Tensor: ...   # kind: "small" | "large"; [N] лет
def loss_fn(model, kind: str, data: torch.Tensor, age: torch.Tensor) -> torch.Tensor: ...
def export_onnx(model, kind: str, path: str) -> float: ...   # возвращает макс. расхождение в годах с PyTorch
```

`data` везде — `float32` [N,3,S,S], RGB, значения 0…255; нормализация внутри модели.

- [ ] **Шаг 1. `requirements.txt`** — восемь строк с версиями из таблицы фактов, без изменений.

- [ ] **Шаг 2. `build_small`, `build_large`.** Проверенный образец — `…spikes/spike_small.py` и `spike_large.py`. `build_small`: `onnx2torch.convert(onnx.load(path))`; сразу после перевода сравнивает выход с ONNX Runtime на случайном входе [2,3,96,96] и бросает `RuntimeError`, если расхождение больше 1e-4. `build_large`: `timm.create_model(LARGE_BACKBONE, pretrained=pretrained, num_classes=101)`; `mean` и `std` берутся из `pretrained_cfg` и умножаются на 255; `forward` возвращает сумму `softmax(logits) × [0…100]`.

- [ ] **Шаг 3. `predict_age`, `loss_fn`.** `small`: возраст = `выход[:, 2] × 100`; потеря — средний модуль разницы с возрастом. `large`: потеря — средний модуль разницы ожидаемого возраста с настоящим **плюс** расхождение Кульбака — Лейблера между `log_softmax(logits)` и нормальным распределением вокруг настоящего возраста (σ = 2 года, по классам 0…100, нормировано в сумму 1). Возраст в потере ограничивается диапазоном 0…100.

- [ ] **Шаг 4. `export_onnx`.** `model.eval()`; `torch.onnx.export(model, (пример,), path, input_names=["data"], output_names=["fc1" для small | "age" для large], dynamic_axes={вход: {0: "N"}, выход: {0: "N"}}, opset_version=17, dynamo=False)`. Затем загружает файл в ONNX Runtime, сравнивает с PyTorch на пакетах размера 1 и 2 (в годах) и бросает `RuntimeError`, если расхождение больше 0,01 года.

- [ ] **Шаг 5. Проверка.** Одной командой Python (из окружения, из каталога `tools/train`): `build_small` на `genderage.onnx` → шаг оптимизатора по `loss_fn` на случайных данных → `export_onnx`; то же для `build_large(False)`.
  Ожидание: обе модели экспортированы; расхождение меньше 0,01; у `small` вход `data` [N,3,96,96] и выход `fc1` [N,3]; у `large` вход `data` [N,3,224,224] и выход `age` [N]; потеря после десяти шагов на одном и том же пакете уменьшилась у обеих.

- [ ] **Шаг 6. Контрольный эксперимент.** Временно испортить в `export_onnx` сравнение (подать в ONNX Runtime вход, умноженный на 0,5) → функция бросает `RuntimeError`. Вернуть; `git diff` показывает только задуманное.

- [ ] **Шаг 7. Коммит** точными путями трёх файлов: «train: модели small и large, потери, экспорт в ONNX».

### Задача B2: чтение папки экспорта, обучение, отчёт

**Файлы:**
- Создать: `tools/train/dataset.py`, `tools/train/train.py`

**Интерфейсы:**
- Использует: всё из `models.py` (B1); папку `E:\Works_Sata\FaceAge-data\export-utk` (A1).
- Создаёт:

```python
@dataclass(frozen=True)
class Sample:
    file: str        # как в metadata.jsonl
    path: Path
    age: float       # (shot − birth) в днях / 365.2425
    sex: str
    pred: float

def read_export(data_dir: str) -> tuple[list[Sample], dict]: ...   # примеры и содержимое export.json
def is_val(file: str) -> bool: ...                                 # zlib.crc32(file.encode()) % 100 < 15
def in_target(age: float) -> bool: ...                             # 13 <= int(age) <= 50
class FaceDataset(torch.utils.data.Dataset): ...                   # (samples, export, input_size, mode) -> (data, age)
def balanced_sampler(samples: list[Sample]) -> torch.utils.data.WeightedRandomSampler: ...
```

`mode`: `"train"` — случайные искажения; `"clean"` — без искажений; `"phone"` — фиксированные искажения.

- [ ] **Шаг 1. `read_export`.** Оборванная или неразбираемая последняя строка отбрасывается молча; строка, чей файл не существует, пропускается, а их общее число печатается одной строкой. Геометрия — из `export.json`, не из констант.

- [ ] **Шаг 2. `FaceDataset`.** «Вид телефона»: центральный квадрат со стороной `size × modelCropScale / cropScale` (219,4 px), приведённый к `input_size` прямой билинейной выборкой **без сглаживания** — так делает `AgeEstimator.Analyze` в телефоне. Сглаживание здесь — ошибка: оно делает вход мягче телефонного, а размытие «омолаживает» лицо (замер ревью B2 на исходной модели: смещение к `pred` −2,31 года со сглаживанием против −0,60 без него). Проход выборки — **один**, прямо из кадра экспорта, со всей геометрией (доля кадра, масштаб, поворот, сдвиг) в одной аффинной матрице; промежуточного вида 219×219 нет: каждый лишний билинейный проход размывает (два прохода давали смещение −1,26 против −0,60 у одного). Остальные искажения применяются после, на разрешении входа; размер «уменьшения с возвратом» — доля входа (train 0,5…1,0, phone 0,6…1,0), а не пиксели из списка ниже. Результат — `float32` [3,S,S], RGB 0…255.
  - `"train"` (значения по умолчанию, исполнитель может уточнить и записать в отчёт): поворот ±12°, масштаб вида ×0,92…1,08, сдвиг ±5 % стороны вида, зеркало с вероятностью 0,5; яркость и контраст ±30 %, насыщенность ±20 %; гамма 0,7…1,4 (вероятность 0,3); размытие σ 0,1…2,0 (0,3); уменьшение до 48…128 px с возвратом (0,4); JPEG с качеством 30…90 (0,3); шум σ до 8 (0,3); чёрная полоса 3…12 % у одного края (0,05).
  - `"phone"`: генератор случайных чисел засеян `crc32(file)`, поэтому искажения одинаковы при каждом запуске: уменьшение до 64…128 px с возвратом, размытие σ 0…1,2, яркость ×0,75…1,25, JPEG 40…80, поворот ±8°.
  - `balanced_sampler`: вес примера = 1 / число примеров с тем же `int(age)` и `sex`.

- [ ] **Шаг 3. `train.py`: параметры.** `--data` (обязателен), `--model small|large` (обязателен), `--out` (обязателен), `--genderage` (по умолчанию `../../src/FaceAge/Resources/Raw/genderage.onnx` относительно скрипта), `--epochs` (30), `--batch` (128), `--lr` (0.001), `--workers` (4), `--limit N` (взять N случайных примеров с фиксированным зерном 0 — для пробных прогонов; первые N не годятся: строки папки идут по возрастанию возраста), `--no-pretrained` (для `large` без скачивания весов), `--resume`. Устройство — `cuda`, если доступна, иначе `cpu`; на `cuda` — смешанная точность.

- [ ] **Шаг 4. `train.py`: обучение и оценка.** Обучающая часть — `not is_val`, все возрасты; AdamW, косинусное расписание с разогревом в одну эпоху. После каждой эпохи — оценка на отложенных примерах с `in_target` в режимах `"clean"` и `"phone"`, с усреднением «кадр + зеркало». Лучшая эпоха — по ошибке в режиме `"phone"`; её веса сохраняются в `--out/best.pt`, после каждой эпохи — `--out/last.pt` (для `--resume`). Строка консоли на эпоху: номер, потеря, MAE clean, MAE phone, время.
  **Ситуация 5:** если отложенных примеров с `in_target` нет — до начала обучения напечатать «В отложенной части нет фото 13–50 лет: считать точность не на чем. Нужен экспорт с этими возрастами.» и выйти с кодом 2.

- [ ] **Шаг 5. `train.py`: базовые линии и сверка `pred`.** На отложенных примерах, режим `"clean"`, вход 96: исходная `genderage.onnx` через ONNX Runtime с усреднением «кадр + зеркало» → «исходная»; она же после поправочной таблицы → «исходная с поправкой». Таблицу скопировать из `AgeEstimator.Calibration` (`src/FaceAge/Vision/AgeEstimator.cs`) вместе с правилом интерполяции из `Analyze`; значения на 2026-11-12: (8.1→2.0), (24.0→22.0), (26.7→25.0), (29.0→26.0), (31.3→26.0), (34.0→28.0), (37.3→30.0), (41.3→34.0), (47.2→39.0), (55.3→47.0), (65.3→55.0), (75.4→66.0).
  Сверка: среднее смещение и средняя абсолютная разница между «исходной» и полем `pred`. Две константы-порога в начале файла, `PRED_BIAS_LIMIT` и `PRED_MAD_LIMIT`; при превышении — заметное предупреждение в консоль и в отчёт, обучение продолжается. Начальные значения 1.0 и 2.0 **предварительные** — их уточняет задача B3.

- [ ] **Шаг 6. `train.py`: результат.** После обучения: загрузить `best.pt` → `export_onnx` в `--out/model.onnx` → `report.md` и `report.json`. В отчёте, по отложенным примерам с `in_target`, для каждой из трёх моделей («исходная», «исходная с поправкой», обученная) и для обоих режимов: MAE; среднее смещение; доля с ошибкой ≤ 1, ≤ 2, ≤ 3, ≤ 5 лет; MAE и смещение по каждому году возраста; MAE по полу. Отдельной таблицей — MAE по возрастам вне 13–50. В конце: результат сверки `pred`, расхождение ONNX с PyTorch, параметры запуска, число примеров в частях.

- [ ] **Шаг 7. Проверка запуском.** `python tools/train/train.py --data E:\Works_Sata\FaceAge-data\export-utk --model small --out E:\Works_Sata\FaceAge-data\runs\b2-small --epochs 1 --limit 3000 --workers 0`.
  Ожидание: код 0; строка эпохи с числами (не `nan`); есть `model.onnx`, `report.md`, `report.json`; в отчёте три модели и оба режима; число обучающих и отложенных примеров в сумме 3 000 (или меньше на число пропавших файлов).

- [ ] **Шаг 8. Контрольные эксперименты.**
  - Ситуация 5: собрать временную папку экспорта из первых 20 строк `export-utk\metadata.jsonl` (все — возраст 1, вне 13–50): скопировать `export.json`, эти строки и их файлы. Запуск на ней → сообщение из шага 4, код возврата 2, отчёта нет. Временную папку удалить.
  - Деление не течёт: одной командой Python проверить, что множества `file` обучающей и отложенной частей не пересекаются и что `is_val` даёт одинаковый ответ в двух запусках процесса.
  - Оценка действительно зависит от модели: временно заменить в оценке выход модели константой 30 → MAE обученной модели в отчёте меняется. Вернуть.

- [ ] **Шаг 9. Коммит** точными путями двух файлов: «train: чтение папки экспорта, обучение, отчёт».

### Задача B3: пробный прогон всей цепочки, пороги сверки, README

**Файлы:**
- Изменить: `tools/train/train.py` (только две константы порогов)
- Создать: `tools/train/README.md`

**Интерфейсы — использует:** `train.py` (B2); собранный `AgeEval` из рабочего дерева ветки A (путь — в журнале); `FACEAGE_AGE_MODEL` (A1).

- [ ] **Шаг 1. Обучение `small` на процессоре.** `--model small --epochs 3 --limit 8000 --out E:\Works_Sata\FaceAge-data\runs\small-dry` (в фоне; оценить время по первой эпохе и записать).
  Ожидание: в `report.md` MAE обученной модели в режиме `"clean"` ниже, чем у «исходной». Если не ниже — это находка, а не повод менять критерий: разобраться и доложить.

- [ ] **Шаг 2. Пороги сверки `pred`.** Из отчёта шага 1 взять измеренные среднее смещение и среднюю абсолютную разницу. Затем две намеренные поломки, по одной, каждая с прогоном `--epochs 1 --limit 3000`:
  1. в `FaceDataset` подать каналы в порядке B,G,R;
  2. взять вид со стороной всего кадра (масштаб 1,75 вместо 1,5).
  Записать значения сверки для обеих. Выставить `PRED_BIAS_LIMIT` и `PRED_MAD_LIMIT` так, чтобы правильный препроцессинг был ниже порога с запасом, а обе поломки — выше. Вернуть поломки; `git diff` показывает только две константы.
  **Если поломку нельзя отличить от правильного варианта никаким порогом — доложить числами.** Это значит, что сверка в нынешнем виде не работает, и решение принимает оркестратор, а не исполнитель.

- [ ] **Шаг 3. Обученная модель в коде телефона.** Из рабочего дерева ветки A, дважды: `dotnet run --project tools/AgeEval -c Release --no-build -- "E:\Works_Sata\FaceAge-data\utk\img1" 0 <временный.csv>` — без переменной и с `FACEAGE_AGE_MODEL = …\runs\small-dry\model.onnx`.
  Ожидание: с новой моделью программа работает, MAE в строке «по умолчанию, без поправки» ниже, чем с исходной. Это проверка, что модель загружается кодом телефона и даёт осмысленный возраст, **а не честная цифра точности**: часть этих фото была в обучении.

- [ ] **Шаг 4. Пробный прогон `large`.** `--model large --no-pretrained --epochs 1 --limit 600 --out …\runs\large-dry`. Ожидание: код 0; у `model.onnx` вход `data` [N,3,224,224] и выход `age` [N]; расхождение ONNX с PyTorch в отчёте меньше 0,01.

- [ ] **Шаг 5. `README.md`** (русский): требования (Windows, видеокарта NVIDIA, Python 3.13); установка — сначала `torch==2.14.1` и `torchvision==0.29.2` в сборке под свою видеокарту по инструкции на сайте PyTorch (на 2026-11-12 для 2.14.1 есть сборки `cu126` и `cu130`), затем `pip install -r requirements.txt`; проверка `torch.cuda.is_available()`; что скопировать с компьютера разработки (`tools/train`, `genderage.onnx`) и куда положить папку экспорта; команды обучения `small` и `large`; как читать `report.md` (какая цифра главная — MAE в режиме «телефон» для 13–50); что значит предупреждение сверки `pred`; что принести обратно (`model.onnx`, `report.md`, `report.json`); что делать при нехватке памяти видеокарты (`--batch`); продолжение прерванного обучения (`--resume`).

- [ ] **Шаг 6. Коммит** точными путями двух файлов: «train: пороги сверки по пробному прогону, README». В отчёте задачи — таблица: время эпохи, MAE трёх моделей, значения сверки для правильного варианта и двух поломок.

---

### Ревью и завершение (волна 4)

- [ ] **Ревью ветки A** (`dotnet-claude-kit:code-reviewer`, `opus`). Диф — файлом. Конкретные риски: (1) пароль не попадает в настройки, журнал и сообщения; (2) таблица путей `ExportRun.Run` — перечислить все пути и убедиться, что ни один не помечает пару завершённой при сбое или отмене; (3) усечение `metadata.jsonl` при возобновлении не теряет строки завершённых пар; (4) в памяти не накапливаются фото. Предупредить ревьюера: конец фрагмента дифа — не конец файла; можно читать соседние файлы.
- [ ] **Ревью ветки B** (`general-purpose`, `opus`). Риски: (1) отложенные примеры не попадают в обучение ни напрямую, ни через выбор лучшей эпохи с утечкой; (2) цифры отчёта считаются только по `in_target` и с тем же усреднением «кадр + зеркало», что в телефоне; (3) препроцессинг в `"clean"` совпадает с телефоном: порядок каналов, диапазон 0…255, доля кадра; (4) экспортированная модель — именно лучшая эпоха.
- [ ] Спорные находки оркестратор проверяет сам до отправки на исправление. Исправления — в коде и в этом плане.
- [ ] **`merge-reviewer`** по обеим веткам относительно `main`: пересечение ожидается только в `tools/AgeEval` (ветка A) — у ветки B общих файлов с A нет, кроме `.gitignore` (меняет только B).
- [ ] Итог заказчику: что сделано, измеренные числа пробного прогона, что не проверено (Oracle, видеокарта, настоящие фото), где ветки. Слияние в `main`, коммит описания и плана, push — только по его решению.

## Что не входит в план

- Этап 3 описания (возврат модели в FaceAge) — отдельный план, когда заказчик принесёт `model.onnx` и отчёт.
- Тесты — нет подтверждения заказчика.
- Запуск на настоящей базе и обучение на видеокарте — выполняет заказчик по двум README.
