namespace WooCast.Cli;

/// <summary>Небольшой набор помощников для аккуратного вывода в консоль.</summary>
internal static class ConsoleUi
{
    private const string Reset = "\u001b[0m";
    private const string Dim = "\u001b[2m";
    private const string Red = "\u001b[31m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Blue = "\u001b[34m";
    private const string Magenta = "\u001b[35m";
    private const string Cyan = "\u001b[36m";
    private const string Bold = "\u001b[1m";
    private static readonly bool Enabled = !Console.IsOutputRedirected;

    public static void Header(string text)
    {
        WriteLine();
        WriteLine($"{Bold}{Magenta}▌ {text}{Reset}");
    }

    public static void Info(string text = "") => WriteLine($"  {text}");

    public static void Detail(string label, string value) =>
        WriteLine($"  {Dim}{label,-22}{Reset}{value}");

    public static void Success(string text) => WriteLine($"  {Green}✓{Reset} {text}");

    public static void Warn(string text) => WriteLine($"  {Yellow}!{Reset} {text}");

    public static void Error(string text) => WriteLine($"  {Red}✗{Reset} {text}");

    public static void Accent(string text) => WriteLine($"  {Cyan}{text}{Reset}");

    /// <summary>Строка команды в справке.</summary>
    public static void Command(string text) => WriteLine($"  {Blue}{Bold}{text}{Reset}");

    /// <summary>Строка, которая перерисовывается на месте (телеметрия).</summary>
    public static void Status(string text)
    {
        if (Enabled)
        {
            Console.Write('\r');
            Console.Write(text.PadRight(Math.Max(0, Console.WindowWidth - 1)));
        }
        else
        {
            WriteLine(text);
        }
    }

    public static void EndStatus()
    {
        if (Enabled)
        {
            Console.WriteLine();
        }
    }

    private static void WriteLine(string text = "") => Console.WriteLine(Enabled ? text : Strip(text));

    private static string Strip(string text)
    {
        var result = text;
        while (result.Contains('\u001b'))
        {
            var start = result.IndexOf('\u001b');
            var end = result.IndexOf('m', start);
            if (end < 0)
            {
                break;
            }

            result = result.Remove(start, end - start + 1);
        }

        return result;
    }

    /// <summary>Строка прогресса в стиле <c>[####----]</c>.</summary>
    public static string Bar(double value, double max, int width = 12)
    {
        if (max <= 0)
        {
            return new string('·', width);
        }

        var filled = (int)Math.Round(Math.Clamp(value / max, 0, 1) * width);
        return new string('█', filled) + new string('·', width - filled);
    }
}
