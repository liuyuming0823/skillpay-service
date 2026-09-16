using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SkillPay.Service.Protocol;

namespace SkillPay.Service.Configuration;

/// <summary>
/// 支付宝 AI 按量付费接入配置。
/// </summary>
public sealed class AipayOptions
{
    public const string SectionName = "Alipay:Aipay";

    /// <summary>沙箱网关。默认值，禁止把生产网关当作沙箱配置使用。</summary>
    public const string SandboxGateway = "https://openapi-sandbox.dl.alipaydev.com/gateway.do";

    /// <summary>生产网关。上线时才显式切换。</summary>
    public const string ProductionGateway = "https://openapi.alipay.com/gateway.do";

    /// <summary>沙箱服务标识。生产环境必须替换为服务市场真实 serviceId。</summary>
    public const string MockServiceId = "api_mock_service_id";

    /// <summary>支付宝网关地址。</summary>
    public string ServerUrl { get; set; } = SandboxGateway;

    /// <summary>应用 APPID。</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 应用私钥（PKCS#1）。本机签名使用，不得外发。
    /// 说明：支付宝 .NET SDK 只接受 <b>无 PEM 头尾</b>的 PKCS#1 原始 Base64；
    /// PKCS#8 会在调用时抛出「不正确的长度」。写入 SDK 时统一走 <see cref="NormalizedPrivateKey"/>。
    /// </summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>支付宝公钥，用于校验支付宝应答签名。</summary>
    public string AlipayPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// 交付给支付宝 SDK 的应用私钥：去掉 PEM 头尾与换行后的纯 Base64。
    /// 配置原值保持不变，仅在实际调用 SDK 时规范化，避免带 PEM 包装导致签名直接失败。
    /// </summary>
    public string NormalizedPrivateKey => SellerSigner.StripPem(PrivateKey);

    /// <summary>交付给支付宝 SDK 的支付宝公钥：同样去掉 PEM 包装。</summary>
    public string NormalizedAlipayPublicKey => SellerSigner.StripPem(AlipayPublicKey);

    /// <summary>商户 ID，2088 开头。</summary>
    public string SellerId { get; set; } = string.Empty;

    /// <summary>商户名称，展示在支付账单上。</summary>
    public string SellerName { get; set; } = string.Empty;

    /// <summary>服务市场 serviceId。沙箱固定为 <see cref="MockServiceId"/>。</summary>
    public string ServiceId { get; set; } = MockServiceId;

    /// <summary>账单币种，本项目固定 CNY。</summary>
    public string Currency { get; set; } = "CNY";

    /// <summary>账单签名算法，协议固定 RSA2。</summary>
    public string SignType { get; set; } = "RSA2";

    /// <summary>账单中 seller_unique_id 对应的字段名，协议固定 seller_id。</summary>
    public string SellerUniqueIdKey { get; set; } = "seller_id";

    /// <summary>订单支付窗口（分钟）。超过该时间未完成支付即视为过期。</summary>
    public int PayWindowMinutes { get; set; } = 30;

    /// <summary>
    /// 精确沙箱模式：仅当网关是沙箱网关且 serviceId 仍是 mock 值时成立。
    /// 该模式下支付宝应答可能不含 amount / resource_id / trade_no，允许按本地订单回填。
    /// </summary>
    public bool IsExactSandboxMode =>
        string.Equals(ServerUrl, SandboxGateway, StringComparison.Ordinal) &&
        string.Equals(ServiceId, MockServiceId, StringComparison.Ordinal);

    /// <summary>是否指向生产网关。生产网关不得与 mock serviceId 混用。</summary>
    public bool IsProductionGateway =>
        string.Equals(ServerUrl, ProductionGateway, StringComparison.Ordinal);
}

/// <summary>
/// 配置校验。缺少关键字段时在启动阶段明确失败，避免运行期行为不确定。
/// </summary>
public sealed class AipayOptionsValidator : IValidateOptions<AipayOptions>
{
    public ValidateOptionsResult Validate(string? name, AipayOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ServerUrl) ||
            !Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out var gateway) ||
            (gateway.Scheme != Uri.UriSchemeHttps && gateway.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add($"{AipayOptions.SectionName}:ServerUrl 必须是合法的 http(s) 绝对地址。");
        }

        Require(options.AppId, nameof(options.AppId), failures);
        Require(options.PrivateKey, nameof(options.PrivateKey), failures);
        Require(options.AlipayPublicKey, nameof(options.AlipayPublicKey), failures);
        Require(options.SellerId, nameof(options.SellerId), failures);
        Require(options.SellerName, nameof(options.SellerName), failures);
        Require(options.ServiceId, nameof(options.ServiceId), failures);

        if (!string.IsNullOrWhiteSpace(options.SellerId) && !options.SellerId.StartsWith("2088", StringComparison.Ordinal))
        {
            failures.Add($"{AipayOptions.SectionName}:SellerId 应为 2088 开头的商户 ID。");
        }

        if (options.PayWindowMinutes is < 1 or > 1440)
        {
            failures.Add($"{AipayOptions.SectionName}:PayWindowMinutes 必须在 1-1440 之间。");
        }

        ValidatePrivateKeyFormat(options, failures);

        // 生产网关配 mock serviceId 是上线前最常见的事故，这里直接拦住。
        if (options.IsProductionGateway &&
            string.Equals(options.ServiceId, AipayOptions.MockServiceId, StringComparison.Ordinal))
        {
            failures.Add(
                $"{AipayOptions.SectionName}:ServerUrl 已指向生产网关，但 ServiceId 仍是 {AipayOptions.MockServiceId}，" +
                "必须替换为服务市场真实 serviceId。");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(string? value, string field, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{AipayOptions.SectionName}:{field} 未配置。");
        }
    }

    /// <summary>
    /// 启动期校验应用私钥形态。
    /// 支付宝 .NET SDK 只接受 PKCS#1 且不带 PEM 头尾的原始 Base64：
    /// 传 PKCS#8 会抛「不正确的长度」，传 PEM 会抛「不是合法的 Base64」。
    /// 这两类错误发生在首次真实付款时才暴露，代价很高，因此在启动阶段直接拦住。
    /// </summary>
    private static void ValidatePrivateKeyFormat(AipayOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.PrivateKey))
        {
            return;
        }

        byte[] keyBytes;

        try
        {
            keyBytes = Convert.FromBase64String(options.NormalizedPrivateKey);
        }
        catch (FormatException)
        {
            failures.Add(
                $"{AipayOptions.SectionName}:PrivateKey 不是合法的 Base64，请确认没有混入说明文字或多余字符。");
            return;
        }

        using var rsa = RSA.Create();

        try
        {
            rsa.ImportRSAPrivateKey(keyBytes, out _);
            return;
        }
        catch (CryptographicException)
        {
            // 继续判断是否为 PKCS#8。
        }

        try
        {
            rsa.ImportPkcs8PrivateKey(keyBytes, out _);

            failures.Add(
                $"{AipayOptions.SectionName}:PrivateKey 是 PKCS#8 格式，而支付宝 .NET SDK 只接受 PKCS#1。" +
                "请用支付宝开放平台密钥工具的「格式转换」把应用私钥转为 PKCS#1 后重新配置。");
        }
        catch (CryptographicException)
        {
            failures.Add(
                $"{AipayOptions.SectionName}:PrivateKey 既不是 PKCS#1 也不是 PKCS#8，不是合法的 RSA 私钥。");
        }
    }
}
