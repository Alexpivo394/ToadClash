namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>Этап выгрузки.</summary>
public enum ExtractStage
{
    /// <summary>Сбор элементов, видимых на виде.</summary>
    Collecting,

    /// <summary>Триангуляция и параметры видимых элементов.</summary>
    Elements,

    /// <summary>Опись всех элементов модели.</summary>
    Inventory
}

/// <summary>Ход выгрузки: этап и сколько элементов этапа обработано.</summary>
/// <param name="Total">Всего элементов этапа; 0 — пока неизвестно.</param>
public sealed record ExtractProgress(ExtractStage Stage, int Done, int Total);
