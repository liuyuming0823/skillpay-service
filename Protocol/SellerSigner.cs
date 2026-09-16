using System.Security.Cryptography;
using System.Text;

namespace SkillPay.Service.Protocol;

/// <summary>
/// 商家账单签名（<c>seller_signature</c>）。签名完全在商家本地完成，不请求支付宝服务端。
/// </summary>
public static class SellerSigner
{
    /// <summary>参与签名的字段，必须按 key 字典序拼接。</summary>
    public static readonly string[] SignedFields =
    [
        "amount",
        "currency",
        "goods_name",
        "out_trade_no",
        "pay_before",
        "resource_id",
        "seller_id",
        "service_id"
    ];

    /// <summary>
    /// 按 key 字典序以 <c>k=v&amp;k=v</c> 拼接后做 RSA2（SHA256withRSA）签名，返回 Base64 结果。
    /// 值为空或全空白的字段不参与拼接。
    /// </summary>
    public static string Sign(IReadOnlyDictionary<string, string> parameters, string privateKey)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKey);

        var content = BuildSignContent(parameters);
        var keyBytes = DecodePrivateKey(privateKey);

        using var rsa = RSA.Create();
        ImportPrivateKey(rsa, keyBytes);

        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(content),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return Convert.ToBase64String(signature);
    }

    /// <summary>构造待签名原文，便于在排查时对账。</summary>
    public static string BuildSignContent(IReadOnlyDictionary<string, string> parameters)
    {
        var builder = new StringBuilder();
        var first = true;

        foreach (var key in parameters.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = parameters[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!first)
            {
                builder.Append('&');
            }

            builder.Append(key).Append('=').Append(value);
            first = false;
        }

        return builder.ToString();
    }

    /// <summary>去掉 PEM 头尾、换行与空白，得到纯 Base64 字符串。</summary>
    public static string StripPem(string key)
    {
        var lines = key
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("-----", StringComparison.Ordinal));

        return string.Concat(lines).Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static byte[] DecodePrivateKey(string privateKey)
    {
        try
        {
            return Convert.FromBase64String(StripPem(privateKey));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("应用私钥不是合法的 Base64 内容，请检查是否误带了换行或说明文字。", ex);
        }
    }

    private static void ImportPrivateKey(RSA rsa, byte[] keyBytes)
    {
        // 优先按 PKCS#1（BEGIN RSA PRIVATE KEY）解析，失败再按 PKCS#8（BEGIN PRIVATE KEY）解析。
        try
        {
            rsa.ImportRSAPrivateKey(keyBytes, out _);
            return;
        }
        catch (CryptographicException)
        {
            // 继续尝试 PKCS#8。
        }

        try
        {
            rsa.ImportPkcs8PrivateKey(keyBytes, out _);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "应用私钥既不是 PKCS#1 也不是 PKCS#8 格式，请使用支付宝官方密钥工具转换后重试。", ex);
        }
    }
}
