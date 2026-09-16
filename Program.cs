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

builder.Services.AddOptions<SkillCatalogOptions>()
    .Bind(builder.Configuration.GetSection(SkillCatalogOptions.SectionName))
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
    string configured = configuration["Storage:DatabasePath"] ?? "data/skillpay.db";
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

    // 解析配置即触发校验器；缺失关键字段会在此处抛错而非运行期失败。
    AipayOptions aipay = provider.GetRequiredService<IOptions<AipayOptions>>().Value;
    SkillCatalogOptions catalog = provider.GetRequiredService<IOptions<SkillCatalogOptions>>().Value;

    logger.LogInformation(
        "skillpay-service 启动：网关={Gateway} appId={AppId} sellerId={SellerId} serviceId={ServiceId} 沙箱模式={SandboxMode}",
        aipay.ServerUrl,
        aipay.AppId,
        aipay.SellerId,
        aipay.ServiceId,
        aipay.IsExactSandboxMode);

    logger.LogInformation(
        "订单存储：{DatabasePath}；已上架技能 {SkillCount} 个：{Skills}",
        db.Database.GetDbConnection().DataSource,
        catalog.Skills.Count,
        string.Join(", ", catalog.Skills.Keys));

    if (!aipay.IsExactSandboxMode)
    {
        logger.LogWarning(
            "当前不是精确沙箱模式，验付将要求支付宝应答字段完整。若尚未签约，请确认 serviceId 与网关配置。");
    }
}
