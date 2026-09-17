namespace SkillPay.Service.Services;

/// <summary>
/// 技能内容生成器。这是「值钱的部分」——实现必须只存在于后端，
/// 不得以任何形式随技能包分发，否则按次计费失去意义。
/// </summary>
/// <remarks>
/// 实现必须满足两条硬约束：
/// <list type="number">
///   <item><b>幂等</b>：同一请求两次调用产出相同内容。订单履约失败用同一
///         <c>Payment-Proof</c> 重试时不得产出不同结果。</item>
///   <item><b>可失败但不得静默降质</b>：抛异常由上层决定降级策略；返回空内容视为失败。</item>
/// </list>
/// 生成在数据库事务之外执行，耗时不受事务约束，但应在 <c>Llm.TimeoutSeconds</c> 内收敛。
/// </remarks>
public interface ISkillContentGenerator
{
    /// <summary>生成器标识，写入响应的 <c>meta.model</c>。</summary>
    string Name { get; }

    /// <summary>按请求生成完整技能内容。</summary>
    /// <param name="request">已校验的生成请求。</param>
    /// <param name="unitPriceCny">该技能单价，用于回填 <c>meta.unit_price_cny</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<SkillContent> GenerateAsync(
        SkillGenerationRequest request,
        decimal unitPriceCny,
        CancellationToken cancellationToken = default);
}
