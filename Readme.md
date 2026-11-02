# ToadClash

Ядро поиска коллизий для [ToadFlow](https://github.com/Alexpivo394/ToadFlow): формат геометрии, правила проверок, движок поиска пересечений и выгрузка геометрии из Revit.

Единицы измерения — миллиметры. Допуск по умолчанию 20 мм. Лицензия MIT, изменения — в [Changelog.md](Changelog.md).

## Пакеты

- `Alexpivo394.Revit.Clash.Geometry` — формат геометрии элементов (сетки, бинарное чтение и запись), без Revit API.
- `Alexpivo394.Revit.Clash.Rules` — правила: наборы выбора, матрица проверок и допуски, без Revit API.
- `Alexpivo394.Revit.Clash.Engine` — движок поиска пересечений сеток по правилам, без Revit API.
- `Alexpivo394.Revit.Clash.Render` — картинки коллизий, без Revit API.
- `Alexpivo394.Revit.Clash.Revit.{год}` — выгрузка геометрии и параметров заданного 3D-вида, по пакету на каждый Revit 2020–2027.

### Версии

Пакеты ядра версионируются вместе: `1.0.0`, `1.1.0` и так далее. Версия пакета выгрузки — год Revit, затем major и minor версии ядра: при ядре `1.0.0` это `2027.1.0`, при `1.0.0-rc.1` — `2027.1.0-rc.1`. Поэтому любая правка выгрузки выходит с новой минорной версией ядра: патч ядра не меняет версию пакета выгрузки.

## Системы координат

- **Слепок модели** хранит координаты в **общей системе координат** документа (площадка `ActiveProjectLocation`). Это та же система, что у выгрузки NWC «по общим координатам»: модели разных разделов совмещаются без пересчёта.
- **Начало слепка.** Позиции вершин записаны во `float` относительно начала `ModelHeader.OriginX/Y/Z`. Начало — внутреннее начало документа, переведённое в общие координаты и округлённое до метра. Оно не зависит от элементов модели, поэтому отпечатки элементов не меняются от выгрузки к выгрузке. Revit держит геометрию в пределах ~32 км от внутреннего начала; элементы дальше 1 км от него считаются в `ExtractResult.FarElements` (там шаг `float` грубее 0,06 мм).
- **Сцена движка.** Модели части должны быть совмещены по общим координатам. Если ядро какой-то модели (габарит 98 % её элементов) лежит дальше 10 м от остальных, прогон завершается `ModelsNotAlignedException` с ключами этих моделей. Движок модели не совмещает: несовмещённая модель — ошибка данных проекта. Одиночные «улетевшие» элементы модель несовмещённой не делают.
- **Точки коллизий** (`ClashHit.PointX/Y/Z`) — в мм общей системы координат.

## Сборка и тесты

```
dotnet build ToadClash.slnx
dotnet test --project tests/Alexpivo394.Revit.Clash.Tests
```

Пакеты собирает пайплайн в `build/` (ModularPipelines, как в ToadExport). Пакеты попадают в `output/`:

```
cd build
DOTNET_ENVIRONMENT=Development Publish__Version=1.0.0-rc.1 dotnet run -c Release -- pack
```

## Публикация

Публикует GitHub Actions (`.github/workflows/PublishRelease.yml`) по тегу версии, например `1.0.0`: собирает пакеты, публикует их на nuget.org и создаёт релиз на GitHub. Ключ nuget.org не хранится: работает trusted publishing (`NuGet/login`, профиль `Alexpivo394`), политику доверия для репозитория настраивают на nuget.org. Каждый push проверяет `.github/workflows/Build.yml`: тесты и сборка выгрузки для Revit 2020 и 2027.

## Служебные инструменты

```powershell
dotnet run -c Release --project tools/ToadClash.Bench -- synth --elements 200000 --models 12 --seed 1 --profile dense --out <каталог>
dotnet run -c Release --project tools/ToadClash.Bench -- run --in <каталог> --threads 8
```

`run` читает `config.json` и пары `.tcg`/`.tcp` из каталога и вложенных каталогов.
Разделы реальных моделей задаются в `model-disciplines.json` по ключу **из заголовка выгрузки**:

```json
{
  "rac_advanced_sample_project": "АР",
  "rme_advanced_sample_project": "ОВ"
}
```

Раздел должен присутствовать в наборах или матрице `config.json`. Для синтетики сохранено определение по ключам вида `АР-1`; ключ `АР` также допустим. Неизвестный раздел вызывает ошибку.

DevAddin публикует каждую выгрузку в новом каталоге `<модель>.toadclash-<идентификатор>` рядом с RVT. Внутри — `model.tcg`, `model.tcp` и журнал. Сначала оба файла записываются во временный каталог `.pending`, затем каталог переименовывается. Bench игнорирует `.pending`; существующие слепки не перезаписываются. `Complete` в отдельном бинарном заголовке описывает завершённость извлечения из Revit; готовность файловой пары определяется публикацией каталога.

Сборка отладочного аддина без установки в Revit:

```powershell
dotnet build tools/ToadClash.DevAddin -c Release.R22 -p:DeployAddin=false
```

Сопоставление двух выгрузок одной модели:

```powershell
dotnet run --project tools/ToadClash.Bench -- identity --old <старый.tcg> --new <новый.tcg>
```

Результат содержит сохранённые ID и предложения сопоставления пересозданных элементов. Это не команда переноса статусов.

Разовое сравнение одного отчёта Navisworks с эквивалентной конфигурацией:

```powershell
dotnet run --project tools/ToadClash.Bench -- navis --report <отчёт.xml> --left <модель-А.tcg> --right <модель-Б.tcg> --left-section ОВ --right-section КР --config <config.json>
```

В присланных XML нет имени исходной модели, поэтому левая и правая модель задаются явно. Рядом с каждым TCG должен лежать TCP. `Unmapped` в отчёте учитываются отдельно от пропущенных пересечений. Конфигурацию сравниваемой проверки нужно задать вручную, включая наборы, исключения и допуски.

DevAddin содержит команду «Создать тестовые модели». Сначала проверьте маленький набор (1 этаж, 2 ячейки), затем полный. Генератор собран, но живой прогон в Revit пока не выполнен.

Дополнительные тесты:

```powershell
dotnet test --project tests/ToadClash.Extractor.Tests -p:ExtractorApiYear=2020
dotnet test --project tests/ToadClash.Extractor.Tests -p:ExtractorApiYear=2021
dotnet test --project tests/ToadClash.Extractor.Tests -p:ExtractorApiYear=2022
dotnet test --project tests/ToadClash.Synthetic.Tests
```
