namespace SkillPay.Service.Domain;

/// <summary>
/// 订单快照。对应 <c>skillpay_orders</c> 表中的一条记录。
/// </summary>
public sealed record OrderSnapshot
{
    public required string OutTradeNo { get; init; }
    public required string Amount { get; init; }
    public required string Currency { get; init; }
    public required string ResourceId { get; init; }
    public required string GoodsName { get; init; }
    public required string PayBefore { get; init; }
    public required string OrderStatus { get; init; }
    public required string FulfillStatus { get; init; }

    /// <summary>已确认履约的交易号；未进入履约前为 <c>null</c>。</summary>
    public string? TradeNo { get; init; }

    /// <summary>已持久化的资源内容；未生成前为 <c>null</c>。</summary>
    public string? ServiceResult { get; init; }
}

/// <summary>
/// 履约准备请求。数据层负责在事务内完成状态迁移与资源落库。
/// </summary>
public sealed record FulfillmentRequest
{
    public required string OutTradeNo { get; init; }
    public required string TradeNo { get; init; }
    public required string ExpectedAmount { get; init; }
    public required string ExpectedResourceId { get; init; }

    /// <summary>
    /// 资源生成器。仅在订单首次进入履约时调用一次。
    /// </summary>
    /// <remarks>
    /// 返回 <see cref="Task{TResult}"/> 而非同步结果：生成可能涉及外部调用（大模型），
    /// 耗时可达数十秒。调用方会在**数据库事务之外**执行它，避免长耗时生成占住写锁。
    /// </remarks>
    public required Func<CancellationToken, Task<string>> CreateResourceAsync { get; init; }
}

/// <summary>
/// 履约准备结果。<see cref="ServiceResult"/> 必须是已持久化的内容，
/// 以便同一凭据重试时返回完全一致的资源。
/// </summary>
public sealed record FulfillmentPreparation
{
    public required string State { get; init; }
    public required string ServiceResult { get; init; }
}

/// <summary>
/// 订单持久化端口。实现必须使用事务与唯一约束，禁止使用进程内内存作为生产存储。
/// </summary>
public interface IOrderRepository
{
    /// <summary>
    /// 在返回 402 之前创建待支付订单。<c>out_trade_no</c> 冲突时必须失败而不是覆盖既有订单。
    /// </summary>
    Task CreatePendingAsync(OrderSnapshot order, CancellationToken cancellationToken = default);

    /// <summary>按商户订单号查询订单；不存在返回 <c>null</c>。</summary>
    Task<OrderSnapshot?> FindByOutTradeNoAsync(string outTradeNo, CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子地准备履约：首次进入时生成并落库资源，已进入履约时复用既有结果。
    /// 订单不存在、资源不匹配或状态不允许时返回 <c>null</c>。
    /// </summary>
    Task<FulfillmentPreparation?> PrepareFulfillmentAsync(FulfillmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>支付宝履约确认成功后标记订单终态。</summary>
    Task MarkFulfilledAsync(string outTradeNo, string tradeNo, CancellationToken cancellationToken = default);
}
