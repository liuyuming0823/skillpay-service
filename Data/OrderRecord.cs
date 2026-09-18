namespace SkillPay.Service.Data;

/// <summary>
/// <c>skillpay_orders</c> 表实体。所有字段均为协议所需的最小集合。
/// </summary>
public sealed class OrderRecord
{
    /// <summary>商户订单号，主键。唯一约束由主键保证。</summary>
    public string OutTradeNo { get; set; } = string.Empty;

    /// <summary>订单金额，已按两位小数规范化。</summary>
    public string Amount { get; set; } = string.Empty;

    /// <summary>币种，本项目固定 <c>CNY</c>。</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>资源标识；与请求的资源必须一致。</summary>
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>账单商品名。</summary>
    public string GoodsName { get; set; } = string.Empty;

    /// <summary>支付截止时间（ISO 8601）。</summary>
    public string PayBefore { get; set; } = string.Empty;

    /// <summary>见 <see cref="Domain.OrderStatus"/>。</summary>
    public string OrderStatus { get; set; } = string.Empty;

    /// <summary>见 <see cref="Domain.FulfillStatus"/>。</summary>
    public string FulfillStatus { get; set; } = string.Empty;

    /// <summary>支付宝交易号；唯一索引，防止同一笔交易重复履约。</summary>
    public string? TradeNo { get; set; }

    /// <summary>已生成的资源内容，用于重试时返回一致结果。</summary>
    public string? ServiceResult { get; set; }

    /// <summary>
    /// 交付物版本号，履约时写入并<b>此后不再变更</b>。
    /// </summary>
    /// <remarks>
    /// 产物本身已随 <see cref="ServiceResult"/> 落库，因此「按订单冻结」在数据上是天然成立的；
    /// 本列把它**显式化**，用于对账、客服排查，以及日后按版本统计交付量。
    /// </remarks>
    public string? DeliveredVersion { get; set; }

    /// <summary>
    /// 首次履约时登记的买家会话标识（<c>Payment-Proof.method.client_session</c>）。
    /// </summary>
    /// <remarks>
    /// 只在首次履约时写入，且**不覆盖**：那一刻请求方刚被支付宝验付通过，
    /// 是「谁付的钱」最可信的一次记录。此后取货都拿它做一致性比对。
    /// 本特性上线前的历史订单该列为 <c>NULL</c>，判定时按「无从判定」放行。
    /// </remarks>
    public string? ClientSession { get; set; }

    /// <summary>会话标识的登记时间。</summary>
    public DateTimeOffset? ClientSessionBoundAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FulfilledAt { get; set; }
}
