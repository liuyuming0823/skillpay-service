using SkillPay.Service.Protocol;

namespace SkillPay.Service.Payments;

/// <summary>
/// <c>alipay.aipay.agent.payment.verify</c> 的调用结果。
/// </summary>
public sealed record PaymentVerifyOutcome
{
    /// <summary>接口是否返回成功（<c>code == 10000</c>）。</summary>
    public required bool ApiSucceeded { get; init; }

    public required string Code { get; init; }
    public string SubCode { get; init; } = string.Empty;
    public string SubMessage { get; init; } = string.Empty;

    /// <summary>凭据是否处于有效状态。必须为 <c>true</c> 才允许交付资源。</summary>
    public bool Active { get; init; }

    public string? TradeNo { get; init; }
    public string? OutTradeNo { get; init; }
    public string? Amount { get; init; }
    public string? ResourceId { get; init; }

    /// <summary>网络或 SDK 层异常（非业务失败），用于日志区分。</summary>
    public bool TransportFailure { get; init; }
}

/// <summary>
/// <c>alipay.aipay.agent.fulfillment.confirm</c> 的调用结果。
/// </summary>
public sealed record FulfillmentConfirmOutcome
{
    public required bool ApiSucceeded { get; init; }
    public string Code { get; init; } = string.Empty;
    public string SubCode { get; init; } = string.Empty;
    public string SubMessage { get; init; } = string.Empty;
    public bool TransportFailure { get; init; }
}

/// <summary>
/// 支付宝 AI 按量付费网关端口。仅暴露验付与履约确认两个出站接口。
/// </summary>
public interface IAlipayGateway
{
    /// <summary>是否处于精确沙箱模式，用于判定应答字段能否按本地订单回填。</summary>
    bool IsExactSandboxMode { get; }

    Task<PaymentVerifyOutcome> VerifyPaymentAsync(PaymentProofData proof, CancellationToken cancellationToken = default);

    Task<FulfillmentConfirmOutcome> ConfirmFulfillmentAsync(string tradeNo, CancellationToken cancellationToken = default);
}
