using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkillPay.Service.Configuration;
using SkillPay.Service.Data;
using SkillPay.Service.Protocol;
using SkillPay.Service.Services;

namespace SkillPay.Service.Endpoints;

/// <summary>
/// 服务端点映射。
/// </summary>
/// <remarks>
/// 提供两种付费入口，二者共用同一套 402 处理逻辑（<see cref="HandlePaidResourceAsync"/>）：
/// <list type="bullet">
///   <item>统一入口 <c>/v1/skills/result</c>：资源编码由请求体 <c>skill_code</c> 或查询参数提供。
///         服务注册时只需登记这一个地址，新增资源无需再注册服务。</item>
///   <item>路径式入口 <c>/v1/skills/{skillCode}/result</c>：地址自含资源编码，便于直接访问与联调。</item>
/// </list>
/// </remarks>
public static class SkillPayEndpoints
{
    /// <summary>统一入口路径。服务注册时登记的「服务地址」应指向此路径。</summary>
    public const string UnifiedPaymentPath = "/v1/skills/result";

    /// <summary>路径式入口模板。</summary>
    public const string PathStylePaymentPath = "/v1/skills/{skillCode}/result";

    /// <summary>技能包版本清单（公开、免鉴权）。</summary>
    public const string SkillManifestPath = "/v1/skills/{skillCode}/manifest";

    /// <summary>技能包下载（公开、免鉴权）。</summary>
    public const string SkillPackagePath = "/v1/skills/{skillCode}/package";

    private const int MaxSkillCodeLength = 64;

    public static IEndpointRouteBuilder MapSkillPayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (IOptions<SkillCatalogOptions> catalog, IOptions<AipayOptions> aipay) =>
        {
            var skills = catalog.Value.Skills
                .Select(pair => new
                {
                    skill_code = pair.Key,
                    price = SafePrice(pair.Value),
                    currency = aipay.Value.Currency,
                    goods_name = pair.Value.GoodsName,
                    description = pair.Value.Description,
                    resource_id = catalog.Value.BuildResourceId(pair.Key),
                    payload_configured = pair.Value.HasPayload
                })
                .ToArray();

            return Results.Ok(new
            {
                service = "skillpay-service",
                protocol = "alipay-aipay-402",
                payment_endpoint = UnifiedPaymentPath,
                payment_endpoint_path_style = PathStylePaymentPath,
                payment_headers = new[] { "Payment-Needed", "Payment-Proof", "Payment-Validation" },
                payment_request = new
                {
                    method = "POST",
                    content_type = "application/json",
                    body = new
                    {
                        skill_code = "<资源编码>",
                        input = new { text = "hello" }
                    }
                },
                skills
            });
        });

        app.MapGet("/healthz", async (SkillPayDbContext db, CancellationToken cancellationToken) =>
        {
            bool databaseReachable = await db.Database.CanConnectAsync(cancellationToken);

            return Results.Json(
                new
                {
                    status = databaseReachable ? "ok" : "degraded",
                    database = databaseReachable ? "reachable" : "unreachable",
                    server_time = DateTimeOffset.UtcNow.ToString("o")
                },
                statusCode: databaseReachable
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable);
        });

        // 统一入口：资源编码来自请求体或查询参数，地址本身不含资源名。
        app.MapPost(UnifiedPaymentPath, HandleUnifiedPaidResourceAsync);
        app.MapGet(UnifiedPaymentPath, HandleUnifiedPaidResourceAsync);

        // 路径式入口：地址自含资源编码。
        app.MapGet(PathStylePaymentPath, HandlePaidResourceAsync);

        // 技能包自更新：公开通道，既不收费也不鉴权。
        // 取货器（技能包）旧了会取不到货，卡住的是用户而不是收入，所以这里不设门槛；
        // 付费门槛只在交付物上，见 /v1/skills/{skillCode}/result。
        app.MapGet(SkillManifestPath, HandleSkillManifestAsync);
        app.MapGet(SkillPackagePath, HandleSkillPackageAsync);

        return app;
    }

    /// <summary>
    /// 统一入口。资源编码取请求体 <c>{"skill_code":"..."}</c>，GET 时取查询参数 <c>?skill_code=</c>。
    /// </summary>
    private static async Task HandleUnifiedPaidResourceAsync(
        HttpContext context,
        PaidAccessService service,
        CancellationToken cancellationToken)
    {
        string? skillCode = await ReadSkillCodeAsync(context, cancellationToken);

        if (string.IsNullOrWhiteSpace(skillCode))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "MISSING_SKILL_CODE",
                "请求缺少资源编码。POST 请在请求体提供 {\"skill_code\":\"...\"}，GET 请使用 ?skill_code=... 。",
                cancellationToken);

            return;
        }

        await HandlePaidResourceAsync(context, skillCode, service, cancellationToken);
    }

    /// <summary>
    /// 付费资源端点。无有效 <c>Payment-Proof</c> 时返回 402 与 <c>Payment-Needed</c> 账单头。
    /// </summary>
    private static async Task HandlePaidResourceAsync(
        HttpContext context,
        string skillCode,
        PaidAccessService service,
        CancellationToken cancellationToken)
    {
        if (!IsValidSkillCode(skillCode))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "INVALID_SKILL_CODE",
                "资源编码只能包含字母、数字、下划线与连字符，且长度不超过 64。",
                cancellationToken);

            return;
        }

        string? paymentProof = context.Request.Headers["Payment-Proof"].FirstOrDefault();

        // 请求体原样透传给交付逻辑，保证「付款时提交的参数」与「交付时使用的参数」一致。
        string? body = await ReadBodyAsync(context, cancellationToken);

        PaidAccessResult result = await service.ExecuteAsync(skillCode, paymentProof, body, cancellationToken);

        await WriteResultAsync(context, result, cancellationToken);
    }

    /// <summary>读取请求体；GET/HEAD 或读取失败时返回 <c>null</c>。</summary>
    private static async Task<string?> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            return null;
        }

        if (context.Request.ContentLength is null or 0)
        {
            return null;
        }

        using var reader = new StreamReader(context.Request.Body, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// 从请求体（POST）或查询参数（GET）读取资源编码。缺失或格式非法时返回 <c>null</c>。
    /// </summary>
    private static async Task<string?> ReadSkillCodeAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            return context.Request.Query["skill_code"].FirstOrDefault();
        }

        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("skill_code", out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            // 请求体不是合法 JSON，按未提供资源编码处理。
            return null;
        }

        return null;
    }

    /// <summary>写出付费访问结果。</summary>
    private static async Task WriteResultAsync(
        HttpContext context,
        PaidAccessResult result,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        foreach (var (name, value) in result.Headers)
        {
            context.Response.Headers[name] = value;
        }

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(result.Payload, ProtocolJson.Options),
            cancellationToken);
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new ErrorBody { Code = code, Message = message }, ProtocolJson.Options),
            cancellationToken);
    }

    /// <summary>
    /// 技能包版本清单：告诉客户端「最新版是多少、要不要强制更新、去哪拿」。
    /// </summary>
    /// <remarks>
    /// 刻意**只返回元信息、不返回包内容**：客户端据此判断是否需要下载，
    /// 也让「检查有没有新版」这件事是一个极轻的请求。
    /// <para>
    /// 分级处置由客户端按两个字段自行判定，服务端只描述事实、不替客户端下判断：
    /// <c>version</c> 是最新版，<c>min_supported_version</c> 是可用的下限。
    /// </para>
    /// </remarks>
    private static async Task HandleSkillManifestAsync(
        HttpContext context,
        string skillCode,
        IOptions<SkillUpdateOptions> skillUpdate,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!TryResolveSkillPackage(skillUpdate.Value, environment, skillCode, out SkillPackageDefinition? definition, out string path, out string failureCode, out string failureMessage))
        {
            await WriteErrorAsync(context, StatusCodeFor(failureCode), failureCode, failureMessage, cancellationToken);
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);

        var manifest = new
        {
            skill_code = skillCode,
            version = definition!.Version,
            min_supported_version = definition.MinSupportedVersion,
            file_name = definition.ResolveFileName(),
            size_bytes = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            package_url = BuildPackageUrl(context, skillUpdate.Value, skillCode),
            notes = definition.Notes,
            published_at = File.GetLastWriteTimeUtc(path).ToString("o")
        };

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";

        // 清单必须反映最新版本，任何中间层缓存都会让客户端长期停在旧版。
        context.Response.Headers.CacheControl = "no-store";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(manifest, ProtocolJson.Options),
            cancellationToken);
    }

    /// <summary>
    /// 下载技能包本体。
    /// </summary>
    /// <remarks>
    /// 与清单一起给出 <c>X-Package-Version</c> 与 <c>X-Package-Sha256</c>，
    /// 便于客户端边下边校验；<c>sha256</c> 同时用作 <c>ETag</c>，内容没变时可省一次传输。
    /// </remarks>
    private static async Task HandleSkillPackageAsync(
        HttpContext context,
        string skillCode,
        IOptions<SkillUpdateOptions> skillUpdate,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!TryResolveSkillPackage(skillUpdate.Value, environment, skillCode, out SkillPackageDefinition? definition, out string path, out string failureCode, out string failureMessage))
        {
            await WriteErrorAsync(context, StatusCodeFor(failureCode), failureCode, failureMessage, cancellationToken);
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string etag = $"\"{sha256}\"";

        // 客户端可能带 If-None-Match 来确认「我这份是不是最新的」，命中就省掉这次传输。
        if (string.Equals(context.Request.Headers.IfNoneMatch.FirstOrDefault(), etag, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/zip";
        context.Response.Headers.ETag = etag;
        context.Response.Headers["X-Package-Version"] = definition!.Version;
        context.Response.Headers["X-Package-Sha256"] = sha256;

        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// 定位技能包：登记检查 → 路径解析（含越界防护）→ 文件存在性检查。
    /// </summary>
    /// <remarks>
    /// 三个失败原因用不同的码分开，是为了让客户端/运维一眼看出该改配置还是该传包，
    /// 而不是笼统回一个 500。
    /// </remarks>
    private static bool TryResolveSkillPackage(
        SkillUpdateOptions options,
        IHostEnvironment environment,
        string skillCode,
        out SkillPackageDefinition? definition,
        out string path,
        out string failureCode,
        out string failureMessage)
    {
        definition = null;
        path = string.Empty;
        failureCode = string.Empty;
        failureMessage = string.Empty;

        definition = options.Resolve(skillCode);

        if (definition is null || string.IsNullOrWhiteSpace(definition.PackageFile))
        {
            failureCode = "SKILL_PACKAGE_NOT_REGISTERED";
            failureMessage = $"技能 {skillCode} 未登记技能包，无法自更新。";
            return false;
        }

        try
        {
            path = ResolvePackagePath(options, environment, definition.PackageFile);
        }
        catch (InvalidOperationException ex)
        {
            failureCode = "SKILL_PACKAGE_PATH_INVALID";
            failureMessage = ex.Message;
            return false;
        }

        if (!File.Exists(path))
        {
            failureCode = "SKILL_PACKAGE_MISSING";
            failureMessage = $"技能 {skillCode} 的包文件不存在：{definition.PackageFile}。请检查 SkillUpdate:PackageRoot 配置。";
            return false;
        }

        return true;
    }

    /// <summary>把配置里的相对路径解析成绝对路径，并强制它落在 <see cref="SkillUpdateOptions.PackageRoot"/> 内。</summary>
    private static string ResolvePackagePath(
        SkillUpdateOptions options,
        IHostEnvironment environment,
        string relative)
    {
        string root = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, options.PackageRoot));

        string full = Path.GetFullPath(
            Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative));

        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        // 穿越防护：路径必须落在 PackageRoot 之内，否则配置写错就能下发任意文件。
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"技能包路径越界：{relative}。只允许 SkillUpdate:PackageRoot 目录内的文件。");
        }

        return full;
    }

    /// <summary>拼出客户端可直接下载的绝对地址。</summary>
    private static string BuildPackageUrl(HttpContext context, SkillUpdateOptions options, string skillCode)
    {
        // 生产上务必显式配置 PublicBaseUrl：反代后面按请求推断会拿到内网地址或错误协议。
        string baseUrl = !string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? options.PublicBaseUrl!.Trim().TrimEnd('/')
            : $"{context.Request.Scheme}://{context.Request.Host}";

        return $"{baseUrl}/v1/skills/{Uri.EscapeDataString(skillCode)}/package";
    }

    private static int StatusCodeFor(string failureCode) => failureCode switch
    {
        "SKILL_PACKAGE_NOT_REGISTERED" => StatusCodes.Status404NotFound,
        "SKILL_PACKAGE_MISSING" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    private static bool IsValidSkillCode(string skillCode)
    {
        if (string.IsNullOrWhiteSpace(skillCode) || skillCode.Length > MaxSkillCodeLength)
        {
            return false;
        }

        foreach (char c in skillCode)
        {
            bool allowed = char.IsAsciiLetterOrDigit(c) || c is '_' or '-';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static string SafePrice(SkillDefinition definition)
    {
        try
        {
            return definition.NormalizedPrice();
        }
        catch (ArgumentException)
        {
            // 配置里的非法金额在启动校验中已拦截；此处仅避免信息接口整体失败。
            return "0.00";
        }
    }
}
