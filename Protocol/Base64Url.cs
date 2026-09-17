using System.Text;

namespace SkillPay.Service.Protocol;

/// <summary>
/// 支付宝 AI 按量付费协议使用的 Base64URL 编解码（无填充，<c>+</c>/<c>/</c> 替换为 <c>-</c>/<c>_</c>）。
/// </summary>
public static class Base64Url
{
    public static string Encode(string value) => Encode(Encoding.UTF8.GetBytes(value));

    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>解码为 UTF-8 字符串；长度非法或 Base64 内容非法时抛出异常。</summary>
    public static string Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            0 => normalized,
            2 => normalized + "==",
            3 => normalized + "=",
            _ => throw new FormatException("Base64URL 长度非法")
        };

        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }

    /// <summary>尽量解码；失败时返回 <c>false</c> 而不抛异常。</summary>
    public static bool TryDecode(string? value, out string decoded)
    {
        decoded = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            decoded = Decode(value);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
