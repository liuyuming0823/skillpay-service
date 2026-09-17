using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkillPay.Service.Services;

/// <summary>
/// 技能工厂「生成技能」请求体。字段名与 <c>POST /v1/skill/generate</c> 契约一致（snake_case）。
/// </summary>
public sealed class SkillGenerationRequest
{
    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web);

    [JsonPropertyName("skill_name")]
    public string? SkillName { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("triggers")]
    public List<string>? Triggers { get; set; }

    [JsonPropertyName("desc_zh")]
    public string? DescZh { get; set; }

    [JsonPropertyName("desc_en")]
    public string? DescEn { get; set; }

    /// <summary>
    /// 解析请求体。返回 <c>false</c> 时 <paramref name="error"/> 为可直接回给调用方的说明。
    /// </summary>
    public static bool TryParse(string? json, out SkillGenerationRequest request, out string error)
    {
        request = new SkillGenerationRequest();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "请求体为空。";
            return false;
        }

        SkillGenerationRequest? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<SkillGenerationRequest>(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            error = $"请求体不是合法 JSON：{ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "请求体不是 JSON 对象。";
            return false;
        }

        if (!IsValidSkillName(parsed.SkillName))
        {
            error = "skill_name 缺失或格式非法：只允许小写字母、数字与连字符，需以字母或数字开头，长度 2-64。";
            return false;
        }

        request = parsed;
        return true;
    }

    /// <summary>技能名规范：市场发布要求小写字母+数字+连字符。</summary>
    public static bool IsValidSkillName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length is < 2 or > 64)
        {
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            bool allowed = char.IsAsciiLetterOrDigit(c) || c == '-';

            if (allowed && char.IsAsciiLetterUpper(c))
            {
                return false;
            }

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>展示名，缺失时用技能名兜底。</summary>
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? SkillName! : DisplayName!.Trim();

    /// <summary>触发词，缺失时用展示名兜底，保证正文与 description 不为空。</summary>
    public IReadOnlyList<string> EffectiveTriggers
    {
        get
        {
            var cleaned = (Triggers ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return cleaned.Length > 0 ? cleaned : new[] { EffectiveDisplayName };
        }
    }

    /// <summary>中文一句话介绍，缺失时用展示名兜底。</summary>
    public string EffectiveDescZh =>
        string.IsNullOrWhiteSpace(DescZh) ? $"调用 {EffectiveDisplayName}" : DescZh!.Trim();

    /// <summary>
    /// 英文一句话介绍。市场规范要求 description_en 不含中文，
    /// 因此缺失或含中文时一律改用由技能名派生的英文句式，不做翻译。
    /// </summary>
    public string EffectiveDescEn
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DescEn) && !ContainsCjk(DescEn!))
            {
                return DescEn!.Trim();
            }

            string words = string.Join(' ', SkillName!
                .Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

            return $"Generates a reusable AI skill for {words}";
        }
    }

    /// <summary>分类，缺失时回落到通用分类（须落在平台枚举内）。</summary>
    public string EffectiveCategory =>
        string.IsNullOrWhiteSpace(Category) ? "office-efficiency" : Category!.Trim();

    private static bool ContainsCjk(string value) =>
        value.Any(c => c >= '\u4e00' && c <= '\u9fff');
}

/// <summary>生成结果。字段名与技能工厂契约一致，由端点层顶层展开返回。</summary>
public sealed class SkillContent
{
    [JsonPropertyName("skill_md")]
    public string SkillMd { get; init; } = string.Empty;

    [JsonPropertyName("references")]
    public Dictionary<string, string> References { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("templates")]
    public Dictionary<string, string> Templates { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("meta")]
    public SkillContentMeta Meta { get; init; } = new();
}

/// <summary>生成元信息，随内容一起返回，便于调用方留痕与对账。</summary>
public sealed class SkillContentMeta
{
    /// <summary>生成来源：模型名或模板标识。</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    /// <summary>生成方式：<c>llm</c> 或 <c>template</c>。</summary>
    [JsonPropertyName("generator")]
    public string Generator { get; init; } = string.Empty;

    /// <summary>消耗 token 数；模板生成时为 0。</summary>
    [JsonPropertyName("tokens")]
    public int Tokens { get; init; }

    /// <summary>本次调用的单价（元），与 402 账单金额一致。</summary>
    [JsonPropertyName("unit_price_cny")]
    public decimal UnitPriceCny { get; init; }
}
