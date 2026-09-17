using SkillPay.Service.Protocol;

namespace SkillPay.Service.Configuration;

/// <summary>
/// 可售资源目录。每个条目对应一个资源标识与单价，资源标识由资源编码派生。
/// </summary>
public sealed class SkillCatalogOptions
{
    public const string SectionName = "SkillCatalog";

    /// <summary>资源标识前缀，用于区分同一服务下的不同资源命名空间。</summary>
    public string ResourceIdPrefix { get; set; } = "skillpay";

    /// <summary>资源定义，key 为资源编码（即请求中的 <c>skill_code</c>）。</summary>
    public Dictionary<string, SkillDefinition> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按资源编码取定义；未配置时返回 <c>null</c>。</summary>
    public SkillDefinition? Resolve(string skillCode) =>
        Skills.TryGetValue(skillCode, out var definition) ? definition : null;

    /// <summary>资源编码对应的资源标识。</summary>
    public string BuildResourceId(string skillCode) => $"{ResourceIdPrefix}:{skillCode}";
}

/// <summary>
/// 单个资源的计费与交付定义。
/// </summary>
public sealed class SkillDefinition
{
    /// <summary>单价（元/次），最多两位小数。</summary>
    public string Price { get; set; } = "0.01";

    /// <summary>账单商品名。用户在支付宝账单上看到的就是它，务必写清楚。</summary>
    public string GoodsName { get; set; } = string.Empty;

    /// <summary>资源说明，仅在服务信息接口 <c>GET /</c> 中返回。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 交付文件路径，相对 <see cref="DeliveryOptions.PayloadRoot"/>。
    /// </summary>
    /// <remarks>
    /// 适合交付安装包、源码压缩包、数据集等。产出会把文件内容以 Base64 内联返回，
    /// 同时给出 SHA-256 与字节数，客户端落盘后应校验摘要。
    /// </remarks>
    public string? PayloadFile { get; set; }

    /// <summary>交付文件名（对外展示）。缺省时取 <see cref="PayloadFile"/> 的文件名。</summary>
    public string? PayloadFileName { get; set; }

    /// <summary>交付文件的 MIME 类型。缺省时按扩展名推断。</summary>
    public string? PayloadMimeType { get; set; }

    /// <summary>
    /// 交付文本。与 <see cref="PayloadFile"/> 二选一，两者同时配置时以文件优先。
    /// </summary>
    /// <remarks>
    /// 适合交付授权码、简短说明、提示词等小内容。
    /// </remarks>
    public string? PayloadText { get; set; }

    /// <summary>校验并返回规范化后的金额。</summary>
    public string NormalizedPrice() => AmountRules.Normalize(Price);

    /// <summary>是否配置了交付物。未配置时服务只返回占位内容，仅供联调。</summary>
    public bool HasPayload =>
        !string.IsNullOrWhiteSpace(PayloadFile) || !string.IsNullOrWhiteSpace(PayloadText);
}
