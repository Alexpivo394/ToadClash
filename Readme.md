# ToadClash

Ядро поиска коллизий для [ToadFlow](https://github.com/Alexpivo394/ToadFlow): формат геометрии, правила проверок, движок поиска пересечений и выгрузка геометрии из Revit.

Единицы измерения: миллиметры, координаты в общей системе координат модели. Допуск по умолчанию 20 мм.

## Пакеты

- `Alexpivo394.Revit.Clash.Geometry` — формат геометрии элементов (сетки, бинарное чтение и запись), без Revit API.
- `Alexpivo394.Revit.Clash.Rules` — правила: наборы выбора, матрица проверок и допуски, без Revit API.
- `Alexpivo394.Revit.Clash.Engine` — движок поиска пересечений сеток по правилам, без Revit API.

Проект выгрузки геометрии из Revit будет добавлен позже.

## Сборка и тесты

```
dotnet build ToadClash.slnx
dotnet test --project tests/Alexpivo394.Revit.Clash.Tests
```
