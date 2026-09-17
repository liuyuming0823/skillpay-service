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
///   <item>统一入口 <c>/v1/skills/result</c>：技能编码由请求体 <c>skill_code</c> 或查询参数提供。
///         服务注册时只需登记这一个地址，新增技能无需再注册服务。</item>
///   <item>路径式入口 <c>/v1/skills/{skillCode}/result</c>：兼容既有调用方与联调脚本。</item>
/// </list>
/// </remarks>
public static class SkillPayEndpoints
{
    /// <summary>统一入口路径。服务注册时登记的「服务地址」应指向此路径。</summary>
    public const string UnifiedPaymentPath = "/v1/skills/result";

    /// <summary>路径式入口模板，仅作兼容保留。</summary>
    public const string PathStylePaymentPath = "/v1/skills/{skillCode}/result";

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
                    resource_id = catalog.Value.BuildResourceId(pair.Key)
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
                        skill_code = "<技能编码>",
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

        // 统一入口：技能编码来自请求体或查询参数，地址本身不含技能名。
        app.MapPost(UnifiedPaymentPath, HandleUnifiedPaidResourceAsync);
        app.MapGet(UnifiedPaymentPath, HandleUnifiedPaidResourceAsync);

        // 路径式入口：兼容既有调用方。
        app.MapGet(PathStylePaymentPath, HandlePaidResourceAsync);

        return app;
    }

    /// <summary>
    /// 统一入口。技能编码取请求体 <c>{"skill_code":"..."}</c>，GET 时取查询参数 <c>?skill_code=</c>。
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
                "请求缺少技能编码。POST 请在请求体提供 {\"skill_code\":\"...\"}，GET 请使用 ?skill_code=... 。",
                cancellationToken);

            return;
        }

        await HandlePaidResourceAsync(context, skillCode, service, cancellationToken);
    }

    /// <summary>
    /// 付费技能资源端点。无有效 <c>Payment-Proof</c> 时返回 402 与 <c>Payment-Needed</c> 账单头。
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
                "技能编码只能包含字母、数字、下划线与连字符，且长度不超过 64。",
                cancellationToken);

            return;
        }

        string? paymentProof = context.Request.Headers["Payment-Proof"].FirstOrDefault();

        PaidAccessResult result = await service.ExecuteAsync(skillCode, paymentProof, cancellationToken);

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

    /// <summary>
    /// 从请求体（POST）或查询参数（GET）读取技能编码。缺失或格式非法时返回 <c>null</c>。
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
            // 请求体不是合法 JSON，按未提供技能编码处理。
            return null;
        }

        return null;
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
