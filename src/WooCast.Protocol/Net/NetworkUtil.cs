using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WooCast.Protocol.Net;

/// <summary>Вспомогательные операции с сетевыми интерфейсами IPv4.</summary>
public static class NetworkUtil
{
    /// <summary>
    /// Широковещательные адреса всех активных IPv4-интерфейсов: глобальный <c>255.255.255.255</c>
    /// и направленный broadcast каждого интерфейса (например <c>192.168.1.255</c>).
    /// </summary>
    /// <remarks>
    /// Направленные адреса важны: многие точки доступа и коммутаторы отбрасывают
    /// глобальный broadcast, а также Windows может выбирать «не тот» интерфейс
    /// (например VPN) при отправке на <c>255.255.255.255</c>.
    /// </remarks>
    public static IReadOnlyList<IPAddress> GetBroadcastAddresses()
    {
        var result = new List<IPAddress> { IPAddress.Broadcast };

        foreach (var (address, mask) in GetUnicastAddresses())
        {
            var directed = GetDirectedBroadcast(address, mask);
            if (directed is not null && !result.Contains(directed))
            {
                result.Add(directed);
            }
        }

        return result;
    }

    /// <summary>Адреса IPv4 всех активных, не loopback-интерфейсов.</summary>
    public static IReadOnlyList<IPAddress> GetLocalAddresses() =>
        GetUnicastAddresses().Select(pair => pair.Address).ToList();

    /// <summary>Наиболее вероятный «основной» адрес машины в локальной сети.</summary>
    public static IPAddress? GetPrimaryLocalAddress()
    {
        var addresses = GetLocalAddresses();

        // Предпочитаем частные диапазоны RFC 1918 — это почти всегда нужный интерфейс.
        var preferred = addresses.FirstOrDefault(IsPrivate);
        return preferred ?? addresses.FirstOrDefault();
    }

    /// <summary>Принадлежит ли адрес частным диапазонам 10/8, 172.16/12, 192.168/16.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true,
            172 => bytes[1] >= 16 && bytes[1] <= 31,
            192 => bytes[1] == 168,
            _ => false,
        };
    }

    /// <summary>Адрес направленного широковещания для пары «адрес + маска».</summary>
    public static IPAddress? GetDirectedBroadcast(IPAddress address, IPAddress? mask)
    {
        if (mask is null || address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var addr = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (addr.Length != 4 || m.Length != 4)
        {
            return null;
        }

        var result = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            result[i] = (byte)(addr[i] | ~m[i]);
        }

        return new IPAddress(result);
    }

    /// <summary>Разобрать строку, введённую пользователем: <c>192.168.1.50</c> или <c>192.168.1.50:47811</c>.</summary>
    /// <returns><see langword="true"/>, если адрес и порт разобраны корректно.</returns>
    public static bool TryParseEndpoint(string? input, int defaultPort, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = defaultPort;

        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var host = text;
        var colon = text.LastIndexOf(':');
        if (colon > 0)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535)
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(host, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static IEnumerable<(IPAddress Address, IPAddress? Mask)> GetUnicastAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(unicast.Address))
                {
                    yield return (unicast.Address, unicast.IPv4Mask);
                }
            }
        }
    }
}
