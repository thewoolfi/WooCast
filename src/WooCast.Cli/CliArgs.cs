namespace WooCast.Cli;

/// <summary>Разбор аргументов командной строки вида <c>--key value</c> и <c>--flag</c>.</summary>
internal sealed class CliArgs
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    private CliArgs()
    {
    }

    public static CliArgs Parse(IReadOnlyList<string> args)
    {
        var result = new CliArgs();

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positional.Add(token);
                continue;
            }

            var key = token[2..];
            var equals = key.IndexOf('=');
            if (equals > 0)
            {
                result._values[key[..equals]] = key[(equals + 1)..];
                continue;
            }

            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result._values[key] = args[++i];
            }
            else
            {
                result._flags.Add(key);
                result._values[key] = null;
            }
        }

        return result;
    }

    public List<string> Positional { get; } = [];

    public bool Has(string key) => _values.ContainsKey(key) || _flags.Contains(key);

    public string? GetString(string key, string? fallback = null) =>
        _values.TryGetValue(key, out var value) && value is not null ? value : fallback;

    public int GetInt(string key, int fallback)
    {
        var text = GetString(key);
        return int.TryParse(text, out var value) ? value : fallback;
    }

    public double GetDouble(string key, double fallback)
    {
        var text = GetString(key);
        return double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    public bool GetBool(string key, bool fallback = false)
    {
        if (_flags.Contains(key))
        {
            return true;
        }

        var text = GetString(key);
        if (text is null)
        {
            return fallback;
        }

        return text.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "да" => true,
            "0" or "false" or "no" or "off" or "нет" => false,
            _ => fallback,
        };
    }

    /// <summary>Разобрать размер вида <c>1920x1080</c>.</summary>
    public (int Width, int Height) GetSize(string key, int fallbackWidth, int fallbackHeight)
    {
        var text = GetString(key);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (fallbackWidth, fallbackHeight);
        }

        var parts = text.Split(['x', 'х', 'X'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
        {
            return (width, height);
        }

        return (fallbackWidth, fallbackHeight);
    }
}
