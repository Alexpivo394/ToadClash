# ToadClash

Ядро поиска коллизий для [ToadFlow](https://github.com/Alexpivo394/ToadFlow): формат геометрии, правила проверок, движок поиска пересечений и выгрузка геометрии из Revit.

Единицы измерения: миллиметры, координаты в общей системе координат модели. Допуск по умолчанию 20 мм.

## Пакеты

- `Alexpivo394.Revit.Clash.Geometry` — формат геометрии элементов (сетки, бинарное чтение и запись), без Revit API.
- `Alexpivo394.Revit.Clash.Rules` — правила: наборы выбора, матрица проверок и допуски, без Revit API.
- `Alexpivo394.Revit.Clash.Engine` — движок поиска пересечений сеток по правилам, без Revit API.

- `Alexpivo394.Revit.Clash.Revit` — выгрузка геометрии и параметров заданного 3D-вида Revit 2020–2027.

## Сборка и тесты

```
dotnet build ToadClash.slnx
dotnet test --project tests/Alexpivo394.Revit.Clash.Tests
```

## Служебные инструменты этапа 0

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

Результаты замеров и оставшиеся ограничения — в `docs/superpowers/reports/`.

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
