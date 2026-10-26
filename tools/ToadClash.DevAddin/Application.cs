using Nice3point.Revit.Toolkit.External;

namespace ToadClash.DevAddin;

/// <summary>Отладочный аддин: панель ToadClash с кнопкой выгрузки на вкладке «Надстройки».</summary>
public sealed class Application : ExternalApplication
{
    public override void OnStartup()
    {
        Application.CreatePanel("ToadClash")
            .AddPushButton<ExtractCommand>("Выгрузить для ToadClash")
            .SetToolTip("Выгружает геометрию выбранного 3D-вида и параметры в .tcg/.tcp рядом с моделью");
    }
}
