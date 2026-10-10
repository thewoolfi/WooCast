using System.Buffers.Binary;

namespace WooCast.Protocol.Media;

/// <summary>Флаги медиа-пакета (<c>flags</c> в заголовке пакета).</summary>
[Flags]
public enum MediaFrameFlags : byte
{
    None = 0,

    /// <summary>Кадр декодируется независимо (IDR).</summary>
    Keyframe = 1,

    /// <summary>Пакет содержит параметры кодирования (SPS/PPS).</summary>
    Config = 2,

    /// <summary>Пакет содержит аудио (зарезервировано под v1.1).</summary>
    Audio = 4,
}

/// <summary>Заголовок медиа-пакета: 16 байт, порядок байт — big-endian.</summary>
/// <param name="PayloadLength">Длина полезной нагрузки.</param>
/// <param name="PtsMicros">Метка времени в микросекундах, монотонно возрастает.</param>
/// <param name="Flags">Флаги пакета.</param>
public readonly record struct MediaPacketHeader(int PayloadLength, long PtsMicros, MediaFrameFlags Flags)
{
    public const int Size = Wcp.MediaPacketHeaderSize;

    public bool IsKeyframe => (Flags & MediaFrameFlags.Keyframe) != 0;

    public bool IsAudio => (Flags & MediaFrameFlags.Audio) != 0;

    /// <summary>Записать заголовок в буфер (не менее <see cref="Size"/> байт).</summary>
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, PayloadLength);
        BinaryPrimitives.WriteInt64BigEndian(destination[4..], PtsMicros);
        destination[12] = (byte)Flags;
        destination[13] = 0;
        destination[14] = 0;
        destination[15] = 0;
    }

    /// <summary>Разобрать заголовок из буфера.</summary>
    /// <exception cref="InvalidDataException">Длина нагрузки вне допустимых пределов.</exception>
    public static MediaPacketHeader Parse(ReadOnlySpan<byte> source)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(source);
        if (length < 0 || length > Wcp.MaxMediaPayload)
        {
            throw new InvalidDataException($"Недопустимая длина медиа-пакета: {length}");
        }

        return new MediaPacketHeader(
            length,
            BinaryPrimitives.ReadInt64BigEndian(source[4..]),
            (MediaFrameFlags)source[12]);
    }

    public static int WriteTo(Stream stream, int payloadLength, long ptsMicros, MediaFrameFlags flags)
    {
        Span<byte> header = stackalloc byte[Size];
        new MediaPacketHeader(payloadLength, ptsMicros, flags).WriteTo(header);
        stream.Write(header);
        return Size;
    }
}
