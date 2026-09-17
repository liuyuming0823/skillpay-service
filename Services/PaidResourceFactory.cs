using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SkillPay.Service.Configuration;

namespace SkillPay.Service.Services;

/// <summary>
/// 付费资源工厂：把一笔已验付的订单兑换成实际交付物。
/// </summary>
/// <remarks>
/// 三种交付形态，按 <see cref="SkillDefinition"/> 的配置选择，优先级由高到低：
/// <list type="number">
///   <item><c>PayloadFile</c> —— 交付 <c>payloads/</c> 下的文件（源码包、安装包、数据集），
///         产出 Base64、字节数与 SHA-256，客户端解码落盘后应校验摘要。</item>
///   <item><c>PayloadText</c> —— 交付一段文本（授权码、简短说明、提示词）。</item>
///   <item>两者都未配置 —— 确定性占位产出，仅用于打通链路与协议自测。</item>
/// </list>
/// <para>
/// 产出由调用方在订单履约时持久化（<b>事务外生成、短事务落库</b>），
/// 同一订单重试会复用同一份内容，因此交付必须对同一订单稳定。
/// </para>
/// </remarks>
public sealed class PaidResourceFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IOptions<SkillCatalogOptions> _catalog;
    private readonly IOptions<DeliveryOptions> _delivery;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PaidResourceFactory> _logger;

    public PaidResourceFactory(
        IOptions<SkillCatalogOptions> catalog,
        IOptions<DeliveryOptions> delivery,
        IHostEnvironment environment,
        ILogger<PaidResourceFactory> logger)
    {
        _catalog = catalog;
        _delivery = delivery;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>按资源编码生成交付内容（JSON 字符串）。</summary>
    /// <param name="resourceId">资源标识。</param>
    /// <param name="outTradeNo">商户订单号，用于把产出与订单绑定。</param>
    /// <param name="skillCode">资源编码，决定交付形态。</param>
    /// <param name="inputJson">原始请求体，供自定义交付逻辑读取参数。</param>
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

        SkillDefinition definition = _catalog.Value.Resolve(skillCode)
            ?? throw new InvalidOperationException($"资源 {skillCode} 未在目录中登记，无法交付。");

        string ticket = ComputeTicket(resourceId, outTradeNo);

        if (!string.IsNullOrWhiteSpace(definition.PayloadFile))
        {
            object fileDelivery = await BuildFileDeliveryAsync(
                definition, resourceId, outTradeNo, skillCode, ticket, cancellationToken);

            return JsonSerializer.Serialize(fileDelivery, SerializerOptions);
        }

        if (!string.IsNullOrWhiteSpace(definition.PayloadText))
        {
            return JsonSerializer.Serialize(
                BuildTextDelivery(definition, resourceId, outTradeNo, skillCode, ticket),
                SerializerOptions);
        }

        _logger.LogWarning(
            "资源 {SkillCode} 未配置交付物（PayloadFile / PayloadText），返回占位内容；生产环境请补齐配置",
            skillCode);

        return JsonSerializer.Serialize(
            BuildPlaceholderContent(resourceId, outTradeNo, skillCode),
            SerializerOptions);
    }

    /// <summary>文件交付：读文件 → 计算摘要 → Base64 内联。</summary>
    private async Task<object> BuildFileDeliveryAsync(
        SkillDefinition definition,
        string resourceId,
        string outTradeNo,
        string skillCode,
        string ticket,
        CancellationToken cancellationToken)
    {
        string path = ResolvePayloadPath(definition.PayloadFile!);

        if (!File.Exists(path))
        {
            // 用户已经付过钱，缺文件属于服务端配置事故，必须显式失败而不是交出空壳。
            throw new FileNotFoundException(
                $"资源 {skillCode} 配置的交付文件不存在：{path}。"
                + "请检查 Delivery:PayloadRoot 与 SkillCatalog:Skills:<code>:PayloadFile 的配置。",
                path);
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        long limit = _delivery.Value.MaxInlinePayloadBytes;

        if (bytes.LongLength > limit)
        {
            throw new InvalidOperationException(
                $"资源 {skillCode} 的交付文件 {bytes.LongLength} 字节，超过内联上限 {limit} 字节。"
                + "请改用对象存储直链交付：把交付物改成含下载地址的文本（PayloadText）。");
        }

        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        return new
        {
            status = "success",
            skill_code = skillCode,
            resource_id = resourceId,
            out_trade_no = outTradeNo,
            ticket,
            delivery = new
            {
                type = "file_base64",
                file_name = ResolveFileName(definition, path),
                mime_type = ResolveMimeType(definition, path),
                size_bytes = bytes.LongLength,
                sha256,
                content_base64 = Convert.ToBase64String(bytes)
            },
            generated_at = DateTimeOffset.UtcNow.ToString("o")
        };
    }

    /// <summary>文本交付。</summary>
    private static object BuildTextDelivery(
        SkillDefinition definition,
        string resourceId,
        string outTradeNo,
        string skillCode,
        string ticket)
    {
        string text = definition.PayloadText!;
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        return new
        {
            status = "success",
            skill_code = skillCode,
            resource_id = resourceId,
            out_trade_no = outTradeNo,
            ticket,
            delivery = new
            {
                type = "text",
                file_name = definition.PayloadFileName ?? $"{skillCode}.txt",
                mime_type = definition.PayloadMimeType ?? "text/plain; charset=utf-8",
                size_bytes = bytes.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                content = text
            },
            generated_at = DateTimeOffset.UtcNow.ToString("o")
        };
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
            delivery = new
            {
                type = "placeholder",
                content = $"资源 {skillCode} 的付费产出（凭据 {digest[..16]}）"
            },
            generated_at = DateTimeOffset.UtcNow.ToString("o")
        };
    }

    /// <summary>
    /// 把配置里的相对路径解析成绝对路径，并强制它落在 <see cref="DeliveryOptions.PayloadRoot"/> 内。
    /// </summary>
    private string ResolvePayloadPath(string relative)
    {
        string root = Path.GetFullPath(
            Path.Combine(_environment.ContentRootPath, _delivery.Value.PayloadRoot));

        string full = Path.GetFullPath(
            Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative));

        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        // 穿越防护：交付路径必须落在 PayloadRoot 之内，否则配置写错就能读出任意文件。
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"交付文件路径越界：{relative}。只允许交付 Delivery:PayloadRoot 目录内的文件。");
        }

        return full;
    }

    private static string ResolveFileName(SkillDefinition definition, string path) =>
        string.IsNullOrWhiteSpace(definition.PayloadFileName)
            ? Path.GetFileName(path)
            : definition.PayloadFileName!.Trim();

    private static string ResolveMimeType(SkillDefinition definition, string path) =>
        string.IsNullOrWhiteSpace(definition.PayloadMimeType)
            ? GuessMimeType(path)
            : definition.PayloadMimeType!.Trim();

    private static string GuessMimeType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".zip" => "application/zip",
            ".7z" => "application/x-7z-compressed",
            ".rar" => "application/vnd.rar",
            ".tar" => "application/x-tar",
            ".gz" or ".tgz" => "application/gzip",
            ".pdf" => "application/pdf",
            ".json" => "application/json; charset=utf-8",
            ".md" => "text/markdown; charset=utf-8",
            ".txt" => "text/plain; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };

    private static string ComputeTicket(string resourceId, string outTradeNo) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{resourceId}|{outTradeNo}")));
}
