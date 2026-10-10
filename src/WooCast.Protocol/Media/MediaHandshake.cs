using System.Buffers.Binary;

namespace WooCast.Protocol.Media;

/// <summary>Приветственный заголовок медиа-соединения: 20 байт, big-endian.</summary>
/// <param name="Version">Версия протокола.</param>
/// <param name="Codec">Идентификатор кодека, см. <see cref="Wcp.Codecs"/>.</param>
/// <param name="Flags">Бит 0 — присутствует аудио (в v1 не используется).</param>
/// <param name="Token">Токен, выданный приёмником в <c>hello_ack</c>. Знаковое 64-битное число.</param>
public readonly record struct MediaHandshake(byte Version, byte Codec, ushort Flags, long Token)
{
    public const int Size = Wcp.MediaHandshakeSize;

    private const ushort FlagHasAudio = 1;

    public bool HasAudio => (Flags & FlagHasAudio) != 0;

    /// <summary>Собрать заголовок в буфер (не менее <see cref="Size"/> байт).</summary>
    public void WriteTo(Span<byte> destination)
    {
        Wcp.MediaMagic.CopyTo(destination);
        destination[4] = Version;
        destination[5] = Codec;
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], Flags);
        BinaryPrimitives.WriteInt64BigEndian(destination[8..], Token);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], 0);
    }

    /// <summary>Создать заголовок для H.264-потока без звука.</summary>
    public static MediaHandshake ForH264(long token, bool hasAudio = false) =>
        new((byte)Wcp.Version, Wcp.Codecs.H264Id, hasAudio ? FlagHasAudio : (ushort)0, token);

    /// <summary>Прочитать и проверить приветственный заголовок из потока.</summary>
    /// <exception cref="InvalidDataException">Сигнатура или версия не совпали.</exception>
    public static async ValueTask<MediaHandshake> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[Size];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (!buffer.AsSpan(0, 4).SequenceEqual(Wcp.MediaMagic))
        {
            throw new InvalidDataException("Медиа-соединение: неверная сигнатура (ожидалось WCM1)");
        }

        var version = buffer[4];
        if (version != Wcp.Version)
        {
            throw new InvalidDataException($"Медиа-соединение: неподдерживаемая версия {version}");
        }

        return new MediaHandshake(
            version,
            buffer[5],
            BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6)),
            BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(8)));
    }
}
