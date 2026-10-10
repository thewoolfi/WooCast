namespace WooCast.Protocol.Media;

/// <summary>Один access unit H.264 в формате Annex-B (с ведущим старт-кодом).</summary>
public sealed class AccessUnit
{
    /// <summary>Байты access unit'а; длина массива равна длине данных.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Содержит IDR-слайс (NAL тип 5) — кадр можно декодировать независимо.</summary>
    public required bool IsKeyframe { get; init; }

    /// <summary>Содержит SPS или PPS (NAL типы 7, 8).</summary>
    public required bool HasParameterSets { get; init; }

    /// <summary>
    /// Граница восстановлена принудительно, потому что поток не содержал AUD.
    /// Признак того, что отправитель собран неправильно — стоит проверить аргументы кодировщика.
    /// </summary>
    public bool ForcedSplit { get; init; }

    public int Length => Data.Length;

    /// <summary>Метка времени кадра в микросекундах; проставляется конвейером захвата.</summary>
    public long PtsMicros { get; set; }
}
