using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SkillPay.Service.Configuration;

namespace SkillPay.Service.Services;

/// <summary>
/// 付费资源生成器：按技能编码分派到对应的产出逻辑。
/// </summary>
/// <remarks>
/// 两种产出形态：
/// <list type="bullet">
///   <item><c>skill-generate</c>：技能内容生成，产出为 <see cref="SkillContent"/> 的 JSON，
///         由「生成技能」端点顶层展开后返回给调用方。</item>
///   <item>其他技能：确定性占位产出，用于联调与协议自测。</item>
/// </list>
/// 生成结果由调用方在订单履约时持久化（**事务外生成、短事务落库**），
/// 同一订单重试时复用同一份内容，因此这里的产出必须对同一订单稳定。
/// </remarks>
public sealed class PaidResourceFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ISkillContentGenerator _generator;
    private readonly IOptions<SkillCatalogOptions> _catalog;

    public PaidResourceFactory(
        ISkillContentGenerator generator,
        IOptions<SkillCatalogOptions> catalog)
    {
        _generator = generator;
        _catalog = catalog;
    }

    /// <summary>按技能编码生成资源内容（JSON 字符串）。</summary>
    /// <param name="resourceId">资源标识。</param>
    /// <param name="outTradeNo">商户订单号，用于绑定产出与订单。</param>
    /// <param name="skillCode">技能编码，决定产出形态。</param>
    /// <param name="inputJson">原始请求体。仅 <c>skill-generate</c> 使用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<string> CreateAsync(
        string resourceId,
        string outTradeNo,
        string skillCode,
        string? inputJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outTradeNo);

        if (string.Equals(skillCode, SkillCatalogOptions.SkillGenerationCode, StringComparison.OrdinalIgnoreCase))
        {
            return await BuildSkillGenerationAsync(skillCode, inputJson, cancellationToken);
        }

        return JsonSerializer.Serialize(
            BuildPlaceholderContent(resourceId, outTradeNo, skillCode),
            SerializerOptions);
    }

    /// <summary>
    /// 生成技能内容。值钱的逻辑全部在 <see cref="ISkillContentGenerator"/> 实现里，
    /// 本方法只负责契约衔接与单价回填。
    /// </summary>
    private async Task<string> BuildSkillGenerationAsync(
        string skillCode,
        string? inputJson,
        CancellationToken cancellationToken)
    {
        if (!SkillGenerationRequest.TryParse(inputJson, out SkillGenerationRequest request, out string error))
        {
            // 端点已在受理请求前完成同样校验；走到这里说明受理与履约的校验口径不一致，
            // 属于编码错误而非用户输入问题，必须显式暴露而不是静默降级。
            throw new InvalidOperationException($"生成请求无法解析：{error}");
        }

        SkillContent content = await _generator.GenerateAsync(
            request,
            ResolveUnitPrice(skillCode),
            cancellationToken);

        return JsonSerializer.Serialize(content, SerializerOptions);
    }

    /// <summary>取该技能的配置单价，用于回填 <c>meta.unit_price_cny</c>。</summary>
    private decimal ResolveUnitPrice(string skillCode)
    {
        SkillDefinition? definition = _catalog.Value.Resolve(skillCode);

        if (definition is null)
        {
            return 0m;
        }

        return decimal.TryParse(
            definition.NormalizedPrice(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out decimal price)
            ? price
            : 0m;
    }

    /// <summary>
    /// 构造占位内容。产出与该订单绑定，保证不同订单结果互不相同，
    /// 且不存在任何无需付费即可访问的固定资源。
    /// </summary>
    private static object BuildPlaceholderContent(string resourceId, string outTradeNo, string skillCode)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{resourceId}|{outTradeNo}")));

        return new
        {
            status = "success",
            skill_code = skillCode,
            resource_id = resourceId,
            out_trade_no = outTradeNo,
            ticket = digest[..32],
            content = $"技能 {skillCode} 的付费产出（凭据 {digest[..16]}）",
            generated_at = DateTimeOffset.UtcNow.ToString("o")
        };
    }
}
