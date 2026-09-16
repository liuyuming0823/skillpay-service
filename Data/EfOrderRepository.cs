using Microsoft.EntityFrameworkCore;
using SkillPay.Service.Domain;

namespace SkillPay.Service.Data;

/// <summary>
/// 基于 EF Core + SQLite 的订单仓储实现。
/// 所有状态迁移都在数据库事务内完成，关键唯一性由主键与唯一索引保证。
/// </summary>
public sealed class EfOrderRepository : IOrderRepository
{
    private readonly SkillPayDbContext _db;
    private readonly ILogger<EfOrderRepository> _logger;

    public EfOrderRepository(SkillPayDbContext db, ILogger<EfOrderRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task CreatePendingAsync(OrderSnapshot order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        var now = DateTimeOffset.UtcNow;
        _db.Orders.Add(new OrderRecord
        {
            OutTradeNo = order.OutTradeNo,
            Amount = order.Amount,
            Currency = order.Currency,
            ResourceId = order.ResourceId,
            GoodsName = order.GoodsName,
            PayBefore = order.PayBefore,
            OrderStatus = order.OrderStatus,
            FulfillStatus = order.FulfillStatus,
            TradeNo = order.TradeNo,
            ServiceResult = order.ServiceResult,
            CreatedAt = now,
            UpdatedAt = now
        });

        // 主键冲突会抛出 DbUpdateException，由上层转成 500，绝不覆盖既有订单。
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<OrderSnapshot?> FindByOutTradeNoAsync(
        string outTradeNo,
        CancellationToken cancellationToken = default)
    {
        var record = await _db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OutTradeNo == outTradeNo, cancellationToken);

        return record is null ? null : ToSnapshot(record);
    }

    public async Task<FulfillmentPreparation?> PrepareFulfillmentAsync(
        FulfillmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var record = await _db.Orders
            .FirstOrDefaultAsync(o => o.OutTradeNo == request.OutTradeNo, cancellationToken);

        if (record is null)
        {
            _logger.LogWarning("履约准备失败：订单不存在 outTradeNo={OutTradeNo}", request.OutTradeNo);
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (!string.Equals(record.ResourceId, request.ExpectedResourceId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "履约准备失败：资源标识不匹配 outTradeNo={OutTradeNo} 本地={Local} 期望={Expected}",
                request.OutTradeNo, record.ResourceId, request.ExpectedResourceId);
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        // 已进入履约的订单直接复用既有资源，保证同一凭据重试结果一致。
        if (!string.IsNullOrEmpty(record.ServiceResult) &&
            (record.FulfillStatus == FulfillStatus.PendingConfirm || record.FulfillStatus == FulfillStatus.Fulfilled))
        {
            await transaction.CommitAsync(cancellationToken);
            return new FulfillmentPreparation
            {
                State = record.FulfillStatus,
                ServiceResult = record.ServiceResult
            };
        }

        if (record.FulfillStatus != FulfillStatus.Unfulfilled)
        {
            _logger.LogWarning(
                "履约准备失败：履约状态非法 outTradeNo={OutTradeNo} fulfillStatus={FulfillStatus}",
                request.OutTradeNo, record.FulfillStatus);
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (record.OrderStatus != OrderStatus.PendingPayment && record.OrderStatus != OrderStatus.Paid)
        {
            _logger.LogWarning(
                "履约准备失败：订单状态非法 outTradeNo={OutTradeNo} orderStatus={OrderStatus}",
                request.OutTradeNo, record.OrderStatus);
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        // 同一订单不允许绑定两笔不同的支付宝交易。
        if (!string.IsNullOrEmpty(record.TradeNo) &&
            !string.Equals(record.TradeNo, request.TradeNo, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "履约准备失败：订单已绑定其他交易 outTradeNo={OutTradeNo} 既有交易={Existing} 本次交易={Incoming}",
                request.OutTradeNo, record.TradeNo, request.TradeNo);
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var generated = request.CreateResource();
        if (string.IsNullOrWhiteSpace(generated))
        {
            _logger.LogError("履约准备失败：资源生成器返回空内容 outTradeNo={OutTradeNo}", request.OutTradeNo);
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        record.ServiceResult = generated;
        record.TradeNo = request.TradeNo;
        record.OrderStatus = OrderStatus.Paid;
        record.FulfillStatus = FulfillStatus.PendingConfirm;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // TradeNo 唯一索引冲突：并发下同一笔交易被两个请求同时履约。
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogWarning(ex, "履约准备并发冲突，回滚后复读 outTradeNo={OutTradeNo}", request.OutTradeNo);

            _db.ChangeTracker.Clear();
            var replay = await _db.Orders
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.OutTradeNo == request.OutTradeNo, cancellationToken);

            if (replay is not null && !string.IsNullOrEmpty(replay.ServiceResult))
            {
                return new FulfillmentPreparation
                {
                    State = replay.FulfillStatus,
                    ServiceResult = replay.ServiceResult
                };
            }

            return null;
        }

        return new FulfillmentPreparation
        {
            State = FulfillStatus.PendingConfirm,
            ServiceResult = generated
        };
    }

    public async Task MarkFulfilledAsync(
        string outTradeNo,
        string tradeNo,
        CancellationToken cancellationToken = default)
    {
        var record = await _db.Orders
            .FirstOrDefaultAsync(o => o.OutTradeNo == outTradeNo, cancellationToken);

        if (record is null)
        {
            _logger.LogWarning("标记履约完成失败：订单不存在 outTradeNo={OutTradeNo}", outTradeNo);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        record.FulfillStatus = FulfillStatus.Fulfilled;
        record.OrderStatus = OrderStatus.Paid;
        record.TradeNo = tradeNo;
        record.FulfilledAt = now;
        record.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static OrderSnapshot ToSnapshot(OrderRecord record) => new()
    {
        OutTradeNo = record.OutTradeNo,
        Amount = record.Amount,
        Currency = record.Currency,
        ResourceId = record.ResourceId,
        GoodsName = record.GoodsName,
        PayBefore = record.PayBefore,
        OrderStatus = record.OrderStatus,
        FulfillStatus = record.FulfillStatus,
        TradeNo = record.TradeNo,
        ServiceResult = record.ServiceResult
    };
}
