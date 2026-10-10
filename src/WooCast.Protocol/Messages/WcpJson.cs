using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WooCast.Protocol.Messages;

/// <summary>Ошибка формата или состояния протокола.</summary>
public sealed class WcpProtocolException : Exception
{
    public WcpProtocolException(string message, string code = Wcp.ErrorCodes.ProtocolError)
        : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>Сериализация сообщений управляющего канала.</summary>
public static class WcpJson
{
    /// <summary>
    /// Настройки по умолчанию. Имена полей — camelCase (<c>Wc</c> → <c>wc</c>), значения
    /// <see langword="null"/> не пишутся в поток, чтобы сообщения оставались компактными.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions IndentedOptions = new(Options) { WriteIndented = true };

    /// <summary>Сериализовать сообщение в одну строку JSON без завершающего перевода строки.</summary>
    public static string Serialize(WcpMessage message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Разобрать строку JSON. Возвращает <see langword="null"/>, если строка пуста.</summary>
    /// <exception cref="WcpProtocolException">Строка не является корректным JSON-объектом.</exception>
    public static WcpMessage? Deserialize(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        WcpMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<WcpMessage>(line, Options);
        }
        catch (JsonException ex)
        {
            throw new WcpProtocolException($"Некорректный JSON в управляющем канале: {ex.Message}");
        }

        if (message is null)
        {
            return null;
        }

        if (message.Wc != 0 && message.Wc != Wcp.Version)
        {
            throw new WcpProtocolException($"Неподдерживаемая версия протокола: {message.Wc}");
        }

        return message;
    }

    /// <summary>Человекочитаемое представление — для логов и отладки.</summary>
    public static string ToPrettyString(WcpMessage message) => JsonSerializer.Serialize(message, IndentedOptions);

    /// <summary>Закодировать сообщение вместе с завершающим <c>\n</c> для записи в TCP-поток.</summary>
    public static byte[] EncodeLine(WcpMessage message) => Encoding.UTF8.GetBytes(Serialize(message) + "\n");
}
