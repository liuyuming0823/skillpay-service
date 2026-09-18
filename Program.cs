using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkillPay.Service.Configuration;
using SkillPay.Service.Data;
using SkillPay.Service.Domain;
using SkillPay.Service.Endpoints;
using SkillPay.Service.Payments;
using SkillPay.Service.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// 支付宝 AI 按量付费配置
// appsettings / 环境变量均可提供；环境变量优先级更高，便于服务器部署时不落盘密钥。
// ---------------------------------------------------------------------------
builder.Services.AddOptions<AipayOptions>()
    .Bind(builder.Configuration.GetSection(AipayOptions.SectionName))
    .PostConfigure(ApplyGatewayEnvironmentOverrides)
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<AipayOptions>, AipayOptionsValidator>();

// ---------------------------------------------------------------------------
// 可售资源目录与交付配置
// ---------------------------------------------------------------------------
builder.Services.AddOptions<SkillCatalogOptions>()
    .Bind(builder.Configuration.GetSection(SkillCatalogOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<DeliveryOptions>()
    .Bind(builder.Configuration.GetSection(DeliveryOptions.SectionName))
    .ValidateOnStart();

// ---------------------------------------------------------------------------
// 技能包自更新（公开通道）
// 技能包本身不收费，谁都能拉最新版；付费门槛只在交付物上。
// ---------------------------------------------------------------------------
builder.Services.AddOptions<SkillUpdateOptions>()
    .Bind(builder.Configuration.GetSection(SkillUpdateOptions.SectionName))
    .ValidateOnStart();

// ---------------------------------------------------------------------------
// 订单持久化：SQLite 文件库。订单必须落库，禁止使用进程内存。
// ---------------------------------------------------------------------------
string databasePath = ResolveDatabasePath(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.AddDbContext<SkillPayDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();

// ---------------------------------------------------------------------------
// 业务服务
// ---------------------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PaidResourceFactory>();
builder.Services.AddSingleton<IAlipayGateway, AlipayGateway>();
builder.Services.AddScoped<PaidAccessService>();

var app = builder.Build();

// ---------------------------------------------------------------------------
// 启动自检：建表 + 强制解析配置与仓储端口，失败即明确终止。
// ---------------------------------------------------------------------------
await StartupSelfCheckAsync(app);

app.MapSkillPayEndpoints();

app.Run();

static void ApplyGatewayEnvironmentOverrides(AipayOptions options)
{
    Apply("ALIPAY_GATEWAY", value => options.ServerUrl = value);
    Apply("ALIPAY_APP_ID", value => options.AppId = value);
    Apply("ALIPAY_SELLER_ID", value => options.SellerId = value);
    Apply("ALIPAY_SELLER_NAME", value => options.SellerName = value);
    Apply("ALIPAY_SERVICE_ID", value => options.ServiceId = value);

    // 密钥常以单行环境变量注入，允许用字面量 \n 表示换行。
    Apply("ALIPAY_PRIVATE_KEY", value => options.PrivateKey = value.Replace("\\n", "\n", StringComparison.Ordinal));
    Apply("ALIPAY_PUBLIC_KEY", value => options.AlipayPublicKey = value.Replace("\\n", "\n", StringComparison.Ordinal));

    static void Apply(string name, Action<string> setter)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        if (!string.IsNullOrWhiteSpace(value))
        {
            setter(value.Trim());
        }
    }
}

static string ResolveDatabasePath(IConfiguration configuration, string contentRootPath)
{
    string configured = configuration["Storage:DatabasePath"] ?? "appdata/skillpay.db";
    string absolute = Path.IsPathRooted(configured)
        ? configured
        : Path.Combine(contentRootPath, configured);

    string? directory = Path.GetDirectoryName(absolute);

    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    return absolute;
}

static async Task StartupSelfCheckAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var provider = scope.ServiceProvider;
    var logger = app.Logger;

    IOrderRepository repository = provider.GetRequiredService<IOrderRepository>();

    if (repository is null)
    {
        throw new InvalidOperationException("IOrderRepository 未绑定，支付流程无法持久化订单。");
    }

    var db = provider.GetRequiredService<SkillPayDbContext>();
    await db.Database.EnsureCreatedAsync();

    // -----------------------------------------------------------------------
    // 幂等补列。
    // EnsureCreatedAsync 只在库不存在时建表，**不会**给已有库补新列；
    // 而生产库里躺着已付款订单，绝不能推倒重建。因此这里按「缺什么补什么」处理，
    // 可重复执行：列已存在则原样跳过，数据一行不动。
    // -----------------------------------------------------------------------
    await EnsureColumnAsync(db, logger, "DeliveredVersion", "TEXT NULL");
    await EnsureColumnAsync(db, logger, "ClientSession", "TEXT NULL");
    await EnsureColumnAsync(db, logger, "ClientSessionBoundAt", "TEXT NULL");

    // 解析配置即触发校验器；缺失关键字段会在此处抛错而非运行期失败。
    AipayOptions aipay = provider.GetRequiredService<IOptions<AipayOptions>>().Value;
    SkillCatalogOptions catalog = provider.GetRequiredService<IOptions<SkillCatalogOptions>>().Value;
    DeliveryOptions delivery = provider.GetRequiredService<IOptions<DeliveryOptions>>().Value;

    logger.LogInformation(
        "skillpay-service 启动：网关={Gateway} appId={AppId} sellerId={SellerId} serviceId={ServiceId} 沙箱模式={SandboxMode}",
        aipay.ServerUrl,
        aipay.AppId,
        aipay.SellerId,
        aipay.ServiceId,
        aipay.IsExactSandboxMode);

    logger.LogInformation(
        "订单存储：{DatabasePath}；已上架资源 {SkillCount} 个：{Skills}",
        db.Database.GetDbConnection().DataSource,
        catalog.Skills.Count,
        string.Join(", ", catalog.Skills.Keys));

    logger.LogInformation(
        "资源交付：文件根目录={PayloadRoot} 履约时限={Timeout}秒 内联上限={Limit}字节",
        delivery.PayloadRoot,
        delivery.FulfillmentTimeoutSeconds,
        delivery.MaxInlinePayloadBytes);

    SkillUpdateOptions skillUpdate = provider.GetRequiredService<IOptions<SkillUpdateOptions>>().Value;

    logger.LogInformation(
        "技能包自更新：目录={PackageRoot} 已登记技能 {Count} 个：{Skills}",
        skillUpdate.PackageRoot,
        skillUpdate.Skills.Count,
        string.Join(", ", skillUpdate.Skills.Keys));

    logger.LogInformation(
        "取货身份校验：模式={Mode}{Hint}",
        aipay.ClaimIdentityMode,
        ClaimIdentityModes.IsEnforce(aipay.ClaimIdentityMode)
            ? string.Empty
            : "（Observe 只记录不拦截，日志确认无误后再切 Enforce）");

    foreach ((string code, SkillDefinition definition) in catalog.Skills)
    {
        if (!definition.HasPayload)
        {
            logger.LogWarning(
                "资源 {SkillCode} 未配置交付物（PayloadFile / PayloadText），只会返回占位内容，不能用于生产",
                code);
        }
    }

    if (!aipay.IsExactSandboxMode)
    {
        logger.LogWarning(
            "当前不是精确沙箱模式，验付将要求支付宝应答字段完整。若尚未签约，请确认 serviceId 与网关配置。");
    }
}

/// <summary>
/// 幂等补列：列已存在就跳过，缺了才 <c>ALTER TABLE</c>。
/// </summary>
/// <remarks>
/// 生产库里躺着已付款订单，任何「重建表」的方案都不可接受，因此这里只做加法。
/// 先 <c>PRAGMA table_info</c> 再决定是否改，可重复执行；
/// 之所以不写成 <c>ADD COLUMN IF NOT EXISTS</c>，是因为 SQLite 本身不支持这个语法。
/// </remarks>
static async Task EnsureColumnAsync(
    SkillPayDbContext db,
    ILogger logger,
    string column,
    string definition)
{
    var connection = db.Database.GetDbConnection();

    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    await using (var probe = connection.CreateCommand())
    {
        probe.CommandText = "PRAGMA table_info('skillpay_orders');";

        await using var reader = await probe.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            // table_info 的第 2 列（索引 1）是列名。
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    await using (var alter = connection.CreateCommand())
    {
        // 列名与类型都来自代码内常量，不拼接任何外部输入。
        alter.CommandText = $"ALTER TABLE \"skillpay_orders\" ADD COLUMN \"{column}\" {definition};";
        await alter.ExecuteNonQueryAsync();
    }

    logger.LogInformation("订单库已补齐列 {Column}", column);
}
