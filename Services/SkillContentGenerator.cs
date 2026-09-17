using Microsoft.Extensions.Options;
using SkillPay.Service.Configuration;

namespace SkillPay.Service.Services;

/// <summary>
/// 内容生成器门面：按配置选大模型或模板，并在大模型不可用时降级。
/// </summary>
/// <remarks>
/// **为什么一定要降级而不是抛错**：生成发生在付款之后。此刻用户的钱已经划走，
/// 5xx 只会让他既拿不到东西、又必须重新走一遍纠纷流程。
/// 因此这里宁可交付「结构完整但内容朴素」的模板产出，也不能空手而归。
/// 降级会打 Error 日志，运维据此发现模型侧故障。
/// </remarks>
public sealed class SkillContentGenerator : ISkillContentGenerator
{
    private readonly GenerationOptions _options;
    private readonly TemplateSkillContentGenerator _template;
    private readonly LlmSkillContentGenerator _llm;
    private readonly ILogger<SkillContentGenerator> _logger;

    public SkillContentGenerator(
        IOptions<GenerationOptions> options,
        TemplateSkillContentGenerator template,
        LlmSkillContentGenerator llm,
        ILogger<SkillContentGenerator> logger)
    {
        _options = options.Value;
        _template = template;
        _llm = llm;
        _logger = logger;
    }

    public string Name => _options.UseLlm ? _llm.Name : _template.Name;

    public async Task<SkillContent> GenerateAsync(
        SkillGenerationRequest request,
        decimal unitPriceCny,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_options.UseLlm)
        {
            return await _template.GenerateAsync(request, unitPriceCny, cancellationToken);
        }

        try
        {
            return await _llm.GenerateAsync(request, unitPriceCny, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消：订单尚未落库，直接上抛即可。
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "大模型生成失败，降级为模板生成 skill={Skill} model={Model} —— 用户已付款，必须交付",
                request.SkillName, _options.Llm.Model);

            return await _template.GenerateAsync(request, unitPriceCny, cancellationToken);
        }
    }
}
