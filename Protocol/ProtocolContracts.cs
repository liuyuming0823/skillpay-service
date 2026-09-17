using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkillPay.Service.Protocol;

/// <summary><c>Payment-Needed</c> 头中 Base64URL 解码后的完整账单结构。</summary>
public sealed class PaymentNeededEnvelope
{
    [JsonPropertyName("protocol")] public PaymentNeededProtocol Protocol { get; init; } = new();

    [JsonPropertyName("method")] public PaymentNeededMethod Method { get; init; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, ProtocolJson.Options);

    public string ToBase64Url() => Base64Url.Encode(ToJson());
}

/// <summary>账单协议字段。构建 402 响应必需。</summary>
public sealed class PaymentNeededProtocol
{
    [JsonPropertyName("out_trade_no")] public string OutTradeNo { get; init; } = string.Empty;

    [JsonPropertyName("amount")] public string Amount { get; init; } = string.Empty;

    [JsonPropertyName("currency")] public string Currency { get; init; } = string.Empty;

    [JsonPropertyName("resource_id")] public string ResourceId { get; init; } = string.Empty;

    [JsonPropertyName("pay_before")] public string PayBefore { get; init; } = string.Empty;

    [JsonPropertyName("seller_signature")] public string SellerSignature { get; init; } = string.Empty;

    [JsonPropertyName("seller_sign_type")] public string SellerSignType { get; init; } = string.Empty;

    [JsonPropertyName("seller_unique_id")] public string SellerUniqueId { get; init; } = string.Empty;
}

/// <summary>账单商户字段。</summary>
public sealed class PaymentNeededMethod
{
    [JsonPropertyName("seller_name")] public string SellerName { get; init; } = string.Empty;

    [JsonPropertyName("seller_id")] public string SellerId { get; init; } = string.Empty;

    [JsonPropertyName("seller_app_id")] public string SellerAppId { get; init; } = string.Empty;

    [JsonPropertyName("goods_name")] public string GoodsName { get; init; } = string.Empty;

    [JsonPropertyName("seller_unique_id_key")] public string SellerUniqueIdKey { get; init; } = string.Empty;

    [JsonPropertyName("service_id")] public string ServiceId { get; init; } = string.Empty;
}

/// <summary>402 响应体。仅用于调试，智能体的支付依据是 <c>Payment-Needed</c> 头。</summary>
public sealed class PaymentRequiredBody
{
    [JsonPropertyName("code")] public string Code { get; init; } = "Payment-Needed";

    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;

    [JsonPropertyName("out_trade_no")] public string OutTradeNo { get; init; } = string.Empty;

    [JsonPropertyName("amount")] public string Amount { get; init; } = string.Empty;

    [JsonPropertyName("currency")] public string Currency { get; init; } = string.Empty;

    [JsonPropertyName("goods_name")] public string GoodsName { get; init; } = string.Empty;
}

/// <summary>验付成功后返回的资源响应体。</summary>
public sealed class ResourceDeliveredBody
{
    [JsonPropertyName("resource_id")] public string ResourceId { get; init; } = string.Empty;

    [JsonPropertyName("content")] public string Content { get; init; } = string.Empty;

    [JsonPropertyName("trade_no")] public string TradeNo { get; init; } = string.Empty;

    [JsonPropertyName("out_trade_no")] public string OutTradeNo { get; init; } = string.Empty;

    [JsonPropertyName("already_fulfilled")] public bool AlreadyFulfilled { get; init; }

    [JsonPropertyName("fulfillment_confirmed")] public bool FulfillmentConfirmed { get; init; }
}

/// <summary><c>Payment-Validation</c> 头内容，供调用方留痕。</summary>
public sealed class PaymentValidationReceipt
{
    [JsonPropertyName("trade_no")] public string TradeNo { get; init; } = string.Empty;

    [JsonPropertyName("out_trade_no")] public string OutTradeNo { get; init; } = string.Empty;

    [JsonPropertyName("validated")] public bool Validated { get; init; } = true;

    [JsonPropertyName("resource_id")] public string ResourceId { get; init; } = string.Empty;
}

/// <summary>统一错误响应体。</summary>
public sealed class ErrorBody
{
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
}

/// <summary>协议 JSON 序列化选项。</summary>
public static class ProtocolJson
{
    /// <summary>
    /// 账单与响应统一使用的序列化选项。
    /// 使用宽松编码器，避免 <c>+</c>（时区偏移）、中文等被转义成 <c>\uXXXX</c>，
    /// 让报文保持人类可读且不依赖解析器对转义的处理方式。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
