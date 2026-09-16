using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SkillPay.Service.Services;

/// <summary>
/// 付费资源生成器。
/// </summary>
/// <remarks>
/// 这是接入真实技能产出逻辑的唯一扩展点：把 <see cref="BuildContent"/> 换成
/// 实际业务计算即可，其余协议与持久化流程无需改动。
/// 生成结果由调用方在订单履约时持久化，同一订单重试时会复用同一份内容，
/// 因此本方法不得依赖随机数或当前时间以外的易变输入。
/// </remarks>
public sealed class PaidResourceFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>按资源标识与商户订单号生成资源内容。</summary>
    public string Create(string resourceId, string outTradeNo, string skillCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outTradeNo);

        return JsonSerializer.Serialize(
            BuildContent(resourceId, outTradeNo, skillCode),
            SerializerOptions);
    }

    /// <summary>
    /// 构造资源内容。默认实现返回与该订单绑定的确定性产出，
    /// 保证不同订单的结果互不相同，且不存在任何无需付费即可访问的固定资源。
    /// </summary>
    private static object BuildContent(string resourceId, string outTradeNo, string skillCode)
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
