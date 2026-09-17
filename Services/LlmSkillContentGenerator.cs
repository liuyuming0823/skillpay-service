using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SkillPay.Service.Configuration;

namespace SkillPay.Service.Services;

/// <summary>
/// 大模型技能内容生成器（OpenAI 兼容 <c>/chat/completions</c>）。
/// </summary>
/// <remarks>
/// 提示词规格以**内嵌资源**形式随程序集分发，不落在磁盘上，也不进入任何技能包。
/// 这是「值钱的部分」的最终形态：买家只能看到请求参数与产出，看不到规格本身。
/// </remarks>
public sealed class LlmSkillContentGenerator : ISkillContentGenerator
{
    private const string SpecResourceName = "SkillPay.Service.Resources.skill-prompt-spec.md";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly GenerationOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmSkillContentGenerator> _logger;
    private readonly Lazy<string> _systemPrompt;

    public LlmSkillContentGenerator(
        IOptions<GenerationOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<LlmSkillContentGenerator> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _systemPrompt = new Lazy<string>(LoadSystemPrompt, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Name => _options.Llm.Model;

    public async Task<SkillContent> GenerateAsync(
        SkillGenerationRequest request,
        decimal unitPriceCny,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmOptions llm = _options.Llm;
        string url = $"{llm.BaseUrl.TrimEnd('/')}/chat/completions";

        var payload = new
        {
            model = llm.Model,
            temperature = llm.Temperature,
            max_tokens = llm.MaxTokens,
            messages = new object[]
            {
                new { role = "system", content = _systemPrompt.Value },
                new { role = "user", content = BuildUserPrompt(request) }
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, Json),
                Encoding.UTF8,
                "application/json")
        };

        httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {llm.ApiKey}");

        using var client = _httpClientFactory.CreateClient(nameof(LlmSkillContentGenerator));
        client.Timeout = TimeSpan.FromSeconds(Math.Max(30, llm.TimeoutSeconds));

        using HttpResponseMessage response = await client.SendAsync(httpRequest, cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"大模型调用失败 HTTP {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        (string content, int tokens) = ReadCompletion(body);
        (string skillMd, Dictionary<string, string> references, Dictionary<string, string> templates) =
            ParseSkillPayload(content, request);

        _logger.LogInformation(
            "大模型生成完成 skill={Skill} model={Model} tokens={Tokens} refs={Refs} templates={Templates}",
            request.SkillName, llm.Model, tokens, references.Count, templates.Count);

        return new SkillContent
        {
            SkillMd = skillMd,
            References = references,
            Templates = templates,
            Meta = new SkillContentMeta
            {
                Model = llm.Model,
                Generator = "llm",
                Tokens = tokens,
                UnitPriceCny = unitPriceCny
            }
        };
    }

    private static string BuildUserPrompt(SkillGenerationRequest request)
    {
        var sb = new StringBuilder();

        sb.AppendLine("按规格生成一个技能，参数如下：");
        sb.AppendLine();
        sb.AppendLine($"- 技能名（kebab-case）：{request.SkillName}");
        sb.AppendLine($"- 中文展示名：{request.EffectiveDisplayName}");
        sb.AppendLine($"- 平台分类：{request.EffectiveCategory}");
        sb.AppendLine($"- 触发词（用户原话）：{string.Join('、', request.EffectiveTriggers)}");
        sb.AppendLine($"- 中文一句话介绍：{request.EffectiveDescZh}");
        sb.AppendLine($"- 英文一句话介绍：{request.EffectiveDescEn}");
        sb.AppendLine();
        sb.AppendLine("只输出 JSON 对象，不要代码块包裹，不要解释。");

        return sb.ToString();
    }

    private (string SkillMd, Dictionary<string, string> References, Dictionary<string, string> Templates)
        ParseSkillPayload(string content, SkillGenerationRequest request)
    {
        string json = ExtractJsonObject(content);

        LlmSkillPayload? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<LlmSkillPayload>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"大模型返回的不是合法 JSON：{ex.Message}");
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.SkillMd))
        {
            throw new InvalidOperationException("大模型返回缺少 skill_md。");
        }

        // 统一 LF：模型输出常带 CRLF，补写的 frontmatter 也是 CRLF，下游写盘会再翻一次。
        string skillMd = TextNormalization.ToLf(EnsureFrontmatter(parsed.SkillMd!, request));

        WarnOnPlaceholders(skillMd);

        return (skillMd, Sanitize(parsed.References, "references"), Sanitize(parsed.Templates, "templates"));
    }

    /// <summary>模型有时会用代码块包裹或加前后缀，截取第一个 <c>{</c> 到最后一个 <c>}</c>。</summary>
    private static string ExtractJsonObject(string content)
    {
        int start = content.IndexOf('{');
        int end = content.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("大模型返回里找不到 JSON 对象。");
        }

        return content[start..(end + 1)];
    }

    /// <summary>
    /// 兜住「模型漏写 frontmatter」导致的 P0 结构问题：缺了就直接补一份合法 frontmatter。
    /// </summary>
    private static string EnsureFrontmatter(string skillMd, SkillGenerationRequest request)
    {
        string trimmed = skillMd.TrimStart();

        if (trimmed.StartsWith("---", StringComparison.Ordinal)
            && trimmed.IndexOf("\n---", 2, StringComparison.Ordinal) > 0)
        {
            return skillMd;
        }

        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine($"name: {request.SkillName}");
        sb.AppendLine($"description: {request.EffectiveDescZh} 触发词：{string.Join('、', request.EffectiveTriggers)}");
        sb.AppendLine("version: 1.0.0");
        sb.AppendLine($"description_zh: {request.EffectiveDescZh}");
        sb.AppendLine($"description_en: {request.EffectiveDescEn}");
        sb.AppendLine($"displayName: {request.EffectiveDisplayName}");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  display_name: {request.EffectiveDisplayName}");
        sb.AppendLine($"  category: {request.EffectiveCategory}");
        sb.AppendLine("  author: 技能工厂");
        sb.AppendLine($"  slug: {request.SkillName}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(skillMd);

        return sb.ToString();
    }

    /// <summary>占位符残留是质量线里明令禁止的，出现即告警，便于据此调提示词。</summary>
    private void WarnOnPlaceholders(string skillMd)
    {
        int angle = skillMd.Count(c => c is '<' or '>');

        if (angle > 0)
        {
            _logger.LogWarning("大模型产出疑似残留占位符：尖括号出现 {Count} 次。", angle);
        }
    }

    private static Dictionary<string, string> Sanitize(Dictionary<string, string>? files, string kind)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (files is null)
        {
            return result;
        }

        foreach ((string key, string value) in files)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // 只取文件名，防止模型给出路径导致越界写入。
            string name = Path.GetFileName(key.Trim());

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            result[name] = TextNormalization.ToLf(value);
        }

        return result;
    }

    private static (string Content, int Tokens) ReadCompletion(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("大模型返回里没有 choices。");
        }

        JsonElement message = choices[0].GetProperty("message");
        string content = message.GetProperty("content").GetString() ?? string.Empty;

        int tokens = 0;

        if (root.TryGetProperty("usage", out JsonElement usage)
            && usage.TryGetProperty("total_tokens", out JsonElement total)
            && total.TryGetInt32(out int value))
        {
            tokens = value;
        }

        return (content, tokens);
    }

    private static string LoadSystemPrompt()
    {
        Assembly assembly = typeof(LlmSkillContentGenerator).Assembly;

        using Stream? stream = assembly.GetManifestResourceStream(SpecResourceName);

        if (stream is null)
        {
            throw new InvalidOperationException(
                $"内嵌提示词规格缺失：{SpecResourceName}。检查 csproj 的 EmbeddedResource 配置。");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed class LlmSkillPayload
    {
        [JsonPropertyName("skill_md")] public string? SkillMd { get; set; }

        [JsonPropertyName("references")] public Dictionary<string, string>? References { get; set; }

        [JsonPropertyName("templates")] public Dictionary<string, string>? Templates { get; set; }
    }
}
