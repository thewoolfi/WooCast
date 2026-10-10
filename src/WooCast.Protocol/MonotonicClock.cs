using System.Diagnostics;

namespace WooCast.Protocol;

/// <summary>
/// Монотонные часы процесса, не зависящие от системного времени.
/// </summary>
/// <remarks>
/// Нужны в двух местах: метки времени кадров (PTS) и поле <c>ts</c> в ping/pong.
/// Обе величины обязаны быть на одной шкале — иначе приёмник не сможет оценить
/// сквозную задержку, сравнивая момент формирования кадра с текущим временем.
/// </remarks>
public static class MonotonicClock
{
    private static readonly double MicrosPerTick = 1_000_000.0 / Stopwatch.Frequency;
    private static readonly double MillisPerTick = 1_000.0 / Stopwatch.Frequency;

    /// <summary>Микросекунды с момента запуска процесса.</summary>
    public static long TimestampMicros => (long)(Stopwatch.GetTimestamp() * MicrosPerTick);

    /// <summary>Миллисекунды с момента запуска процесса.</summary>
    public static long TimestampMillis => (long)(Stopwatch.GetTimestamp() * MillisPerTick);
}
