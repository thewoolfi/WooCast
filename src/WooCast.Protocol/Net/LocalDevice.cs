using System.Security.Cryptography;
using System.Text;

namespace WooCast.Protocol.Net;

/// <summary>Идентификация нашего ПК в протоколе.</summary>
public static class LocalDevice
{
    private static readonly Lazy<string> LazyId = new(ComputeId);

    /// <summary>Имя компьютера — показывается на телевизоре в списке источников.</summary>
    public static string Name
    {
        get
        {
            var name = Environment.MachineName;
            return string.IsNullOrWhiteSpace(name) ? "Компьютер" : name;
        }
    }

    /// <summary>
    /// Стабильный идентификатор устройства: первые 16 шестнадцатеричных знаков SHA-256 от имени
    /// компьютера. Одинаков между запусками, поэтому приёмник может узнавать уже знакомый ПК.
    /// </summary>
    public static string Id => LazyId.Value;

    private static string ComputeId()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Name));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
