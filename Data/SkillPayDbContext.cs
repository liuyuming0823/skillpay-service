using Microsoft.EntityFrameworkCore;

namespace SkillPay.Service.Data;

/// <summary>
/// 订单持久化上下文。默认使用 SQLite 文件库，可替换为项目既有数据库。
/// </summary>
public sealed class SkillPayDbContext : DbContext
{
    public SkillPayDbContext(DbContextOptions<SkillPayDbContext> options) : base(options)
    {
    }

    public DbSet<OrderRecord> Orders => Set<OrderRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<OrderRecord>();

        order.ToTable("skillpay_orders");
        order.HasKey(o => o.OutTradeNo);

        order.Property(o => o.OutTradeNo).HasMaxLength(64).IsRequired();
        order.Property(o => o.Amount).HasMaxLength(32).IsRequired();
        order.Property(o => o.Currency).HasMaxLength(8).IsRequired();
        order.Property(o => o.ResourceId).HasMaxLength(256).IsRequired();
        order.Property(o => o.GoodsName).HasMaxLength(256).IsRequired();
        order.Property(o => o.PayBefore).HasMaxLength(64).IsRequired();
        order.Property(o => o.OrderStatus).HasMaxLength(32).IsRequired();
        order.Property(o => o.FulfillStatus).HasMaxLength(32).IsRequired();
        order.Property(o => o.TradeNo).HasMaxLength(64);
        order.Property(o => o.ServiceResult);

        // 同一笔支付宝交易只能履约一次。SQLite 的唯一索引允许多个 NULL，
        // 因此未进入履约的订单不受影响。
        order.HasIndex(o => o.TradeNo).IsUnique();
        order.HasIndex(o => o.ResourceId);
        order.HasIndex(o => o.FulfillStatus);
    }
}
