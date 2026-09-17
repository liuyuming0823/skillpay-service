namespace SkillPay.Service.Configuration;

/// <summary>
/// 技能内容生成配置。这是「值钱的部分」的开关与凭据所在，只存在于服务端。
/// </summary>
/// <remarks>
/// <c>Mode</c> 为 <c>llm</c> 时走大模型生成；为 <c>template</c>（默认）时走确定性模板，
/// 用于在没有模型凭据的环境下把整条付费链路跑通。两种模式产出结构一致。
/// </remarks>
public sealed class GenerationOptions
{
    public const string SectionName = "Generation";

    /// <summary>生成模式：<c>llm</c> 或 <c>template</c>。</summary>
    public string Mode { get; set; } = "template";

    /// <summary>技能内容生成的提示词规格，作为系统提示词使用。</summary>
    public string SystemPromptPath { get; set; } = "Resources/skill-prompt-spec.md";

    /// <summary>大模型配置。</summary>
    public LlmOptions Llm { get; set; } = new();

    /// <summary>是否走大模型生成。</summary>
    public bool UseLlm =>
        string.Equals(Mode, "llm", StringComparison.OrdinalIgnoreCase) && Llm.IsConfigured;
}

/// <summary>OpenAI 兼容的大模型接入配置。</summary>
public sealed class LlmOptions
{
    /// <summary>兼容 <c>/chat/completions</c> 的服务地址，例如 <c>https://api.deepseek.com/v1</c>。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API Key。只从环境变量或密钥管理注入，禁止写进配置仓库。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>模型名。</summary>
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>单次调用超时（秒）。生成整份技能耗时较长，默认放宽到 180。</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>采样温度。生成结构化文档宜低不宜高。</summary>
    public decimal Temperature { get; set; } = 0.3m;

    /// <summary>单次生成的最大输出 token 数。</summary>
    public int MaxTokens { get; set; } = 8192;

    /// <summary>凭据是否齐备。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
