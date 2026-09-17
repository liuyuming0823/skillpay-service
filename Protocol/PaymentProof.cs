using System.Text.Json;

namespace SkillPay.Service.Protocol;

/// <summary>
/// <c>Payment-Proof</c> 头解析后的内容。
/// </summary>
public sealed record PaymentProofData
{
    public required string PaymentProof { get; init; }
    public required string TradeNo { get; init; }
    public string? ClientSession { get; init; }
}

/// <summary>
/// <c>Payment-Proof</c> 头解析器。任何字段缺失或格式非法都必须判定为解析失败，
/// 由上层退回 402 重新发起支付。
/// </summary>
public static class PaymentProofReader
{
    public static bool TryRead(string? headerValue, out PaymentProofData? data, out string error)
    {
        data = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(headerValue))
        {
            error = "缺少 Payment-Proof 头";
            return false;
        }

        if (!Base64Url.TryDecode(headerValue, out var json))
        {
            error = "Payment-Proof 不是合法的 Base64URL 内容";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("protocol", out var protocol) ||
                protocol.ValueKind != JsonValueKind.Object)
            {
                error = "Payment-Proof 缺少 protocol 节点";
                return false;
            }

            var paymentProof = ReadString(protocol, "payment_proof");
            var tradeNo = ReadString(protocol, "trade_no");

            if (string.IsNullOrWhiteSpace(paymentProof))
            {
                error = "Payment-Proof 缺少 protocol.payment_proof";
                return false;
            }

            if (string.IsNullOrWhiteSpace(tradeNo))
            {
                error = "Payment-Proof 缺少 protocol.trade_no";
                return false;
            }

            string? clientSession = null;
            if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.Object)
            {
                clientSession = ReadString(method, "client_session");
            }

            data = new PaymentProofData
            {
                PaymentProof = paymentProof,
                TradeNo = tradeNo,
                ClientSession = string.IsNullOrWhiteSpace(clientSession) ? null : clientSession
            };

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Payment-Proof 不是合法 JSON：{ex.Message}";
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
