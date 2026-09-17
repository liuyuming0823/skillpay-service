namespace SkillPay.Service.Domain;

/// <summary>
/// 订单支付状态。取值与支付宝 AI 按量付费协议一致。
/// </summary>
public static class OrderStatus
{
    /// <summary>已创建待支付订单，尚未收到有效付款凭据。</summary>
    public const string PendingPayment = "PENDING_PAYMENT";

    /// <summary>支付凭据已验证通过。</summary>
    public const string Paid = "PAID";
}

/// <summary>
/// 履约状态。取值与支付宝 AI 按量付费协议一致。
/// </summary>
public static class FulfillStatus
{
    /// <summary>资源尚未生成。</summary>
    public const string Unfulfilled = "UNFULFILLED";

    /// <summary>资源已生成并持久化，尚未取得支付宝履约确认。</summary>
    public const string PendingConfirm = "PENDING_CONFIRM";

    /// <summary>支付宝已确认履约，订单终态。</summary>
    public const string Fulfilled = "FULFILLED";
}
