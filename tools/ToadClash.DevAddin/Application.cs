using Nice3point.Revit.Toolkit.External;

namespace ToadClash.DevAddin;

/// <summary>Отладочный аддин: панель ToadClash с кнопкой выгрузки на вкладке «Надстройки».</summary>
public sealed class Application : ExternalApplication
{
    public override void OnStartup()
    {
        var panel = Application.CreatePanel("ToadClash");
        panel.AddPushButton<ExtractCommand>("Выгрузить для ToadClash")
            .SetToolTip("Выгружает геометрию выбранного 3D-вида и параметры в .tcg/.tcp рядом с моделью");
        panel.AddPushButton<GenerateCommand>("Создать тестовые модели")
            .SetToolTip("Создаёт новые модели в отдельном каталоге ToadClashData/synthetic/revit");
    }
}
