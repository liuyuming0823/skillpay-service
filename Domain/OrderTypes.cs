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

    /// <summary>交付物版本号；未履约前为 <c>null</c>，履约后固定不再变更。</summary>
    public string? DeliveredVersion { get; init; }

    /// <summary>首次履约时登记的买家会话标识；本特性上线前的历史订单为 <c>null</c>。</summary>
    public string? ClientSession { get; init; }

    /// <summary>会话标识的登记时间。</summary>
    public DateTimeOffset? ClientSessionBoundAt { get; init; }
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
    /// 本次取货请求携带的买家会话标识（<c>Payment-Proof.method.client_session</c>）。
    /// </summary>
    /// <remarks>
    /// 首次履约时会被登记到订单上；之后每次取货都用它比对，判断「取货人是否就是付款人」。
    /// </remarks>
    public string? ClientSession { get; init; }

    /// <summary>
    /// 本次交付的产物版本号。落库后即冻结，用于对账与版本统计。
    /// </summary>
    public string? PayloadVersion { get; init; }

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

    /// <summary>本订单实际交付的产物版本号；历史数据可能为空。</summary>
    public string? DeliveredVersion { get; init; }
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
    /// 按支付宝交易号查询订单；不存在返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 重取链路里必须先按交易号找到已交付订单，才能走「不重复验付」的快路径。
    /// 交易号上有唯一索引，因此最多命中一条。
    /// </remarks>
    Task<OrderSnapshot?> FindByTradeNoAsync(string tradeNo, CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子地准备履约：首次进入时生成并落库资源，已进入履约时复用既有结果。
    /// 订单不存在、资源不匹配或状态不允许时返回 <c>null</c>。
    /// </summary>
    Task<FulfillmentPreparation?> PrepareFulfillmentAsync(FulfillmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>支付宝履约确认成功后标记订单终态。</summary>
    Task MarkFulfilledAsync(string outTradeNo, string tradeNo, CancellationToken cancellationToken = default);
}
