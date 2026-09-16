using SkillPay.Service.Protocol;

namespace SkillPay.Service.Configuration;

/// <summary>
/// 可售技能目录。每个技能对应一个资源标识与单价，资源标识由技能编码派生。
/// </summary>
public sealed class SkillCatalogOptions
{
    public const string SectionName = "SkillCatalog";

    /// <summary>资源标识前缀，用于区分同一服务下的不同资源命名空间。</summary>
    public string ResourceIdPrefix { get; set; } = "skillpay";

    /// <summary>技能定义，key 为技能编码。</summary>
    public Dictionary<string, SkillDefinition> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按技能编码取定义；未配置时返回 <c>null</c>。</summary>
    public SkillDefinition? Resolve(string skillCode) =>
        Skills.TryGetValue(skillCode, out var definition) ? definition : null;

    /// <summary>技能编码对应的资源标识。</summary>
    public string BuildResourceId(string skillCode) => $"{ResourceIdPrefix}:{skillCode}";
}

/// <summary>
/// 单个技能的计费定义。
/// </summary>
public sealed class SkillDefinition
{
    /// <summary>单价（元/次），最多两位小数。</summary>
    public string Price { get; set; } = "0.01";

    /// <summary>账单商品名。</summary>
    public string GoodsName { get; set; } = string.Empty;

    /// <summary>技能说明，仅在服务信息接口中返回。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>校验并返回规范化后的金额。</summary>
    public string NormalizedPrice() => AmountRules.Normalize(Price);
}
