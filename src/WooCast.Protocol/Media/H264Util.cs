namespace WooCast.Protocol.Media;

/// <summary>Разбор структуры потока H.264 (Annex-B) без полного декодирования.</summary>
public static class H264Util
{
    public const int NalTypeSlice = 1;
    public const int NalTypeIdr = 5;
    public const int NalTypeSei = 6;
    public const int NalTypeSps = 7;
    public const int NalTypePps = 8;
    public const int NalTypeAud = 9;

    /// <summary>Длина старт-кода в позиции <paramref name="offset"/> или 0, если старт-кода нет.</summary>
    public static int StartCodeLength(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 3 > data.Length || data[offset] != 0 || data[offset + 1] != 0)
        {
            return 0;
        }

        if (data[offset + 2] == 1)
        {
            return 3;
        }

        if (offset + 4 <= data.Length && data[offset + 2] == 0 && data[offset + 3] == 1)
        {
            return 4;
        }

        return 0;
    }

    /// <summary>Определить, является ли access unit ключевым и содержит ли он параметры кодирования.</summary>
    public static void Analyze(ReadOnlySpan<byte> data, out bool isKeyframe, out bool hasParameterSets)
    {
        isKeyframe = false;
        hasParameterSets = false;

        var offset = 0;
        while (offset < data.Length)
        {
            var startCode = StartCodeLength(data, offset);
            if (startCode == 0)
            {
                offset++;
                continue;
            }

            var headerIndex = offset + startCode;
            if (headerIndex >= data.Length)
            {
                break;
            }

            switch (data[headerIndex] & 0x1F)
            {
                case NalTypeIdr:
                    isKeyframe = true;
                    break;
                case NalTypeSps:
                case NalTypePps:
                    hasParameterSets = true;
                    break;
            }

            offset = headerIndex + 1;
        }
    }

    /// <summary>Содержит ли access unit AUD (NAL тип 9).</summary>
    public static bool HasAud(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var startCode = StartCodeLength(data, offset);
            if (startCode == 0)
            {
                offset++;
                continue;
            }

            var headerIndex = offset + startCode;
            if (headerIndex >= data.Length)
            {
                return false;
            }

            if ((data[headerIndex] & 0x1F) == NalTypeAud)
            {
                return true;
            }

            offset = headerIndex + 1;
        }

        return false;
    }

    /// <summary>
    /// Извлечь SPS и PPS в формате Annex-B (вместе со старт-кодами) — именно в таком виде
    /// их принимает <c>MediaCodec</c> в качестве <c>csd-0</c> и <c>csd-1</c>.
    /// </summary>
    /// <returns><see langword="true"/>, если найден хотя бы один из наборов.</returns>
    public static bool TryExtractParameterSets(
        ReadOnlySpan<byte> data,
        out byte[]? sps,
        out byte[]? pps)
    {
        sps = null;
        pps = null;

        var offset = 0;
        while (offset < data.Length)
        {
            var startCode = StartCodeLength(data, offset);
            if (startCode == 0)
            {
                offset++;
                continue;
            }

            var headerIndex = offset + startCode;
            if (headerIndex >= data.Length)
            {
                break;
            }

            var nalType = data[headerIndex] & 0x1F;
            if (nalType is NalTypeSps or NalTypePps)
            {
                var end = FindNextStartCode(data, headerIndex + 1);
                var nal = data[offset..end].ToArray();

                if (nalType == NalTypeSps && sps is null)
                {
                    sps = nal;
                }
                else if (nalType == NalTypePps && pps is null)
                {
                    pps = nal;
                }

                if (sps is not null && pps is not null)
                {
                    return true;
                }
            }

            offset = headerIndex + 1;
        }

        return sps is not null || pps is not null;
    }

    private static int FindNextStartCode(ReadOnlySpan<byte> data, int from)
    {
        for (var i = from; i + 3 <= data.Length; i++)
        {
            if (StartCodeLength(data, i) > 0)
            {
                return i;
            }
        }

        return data.Length;
    }
}
