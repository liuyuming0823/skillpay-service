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
public static class SkillPayEndpoints
{
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
                payment_endpoint = "/v1/skills/{skillCode}/result",
                payment_headers = new[] { "Payment-Needed", "Payment-Proof", "Payment-Validation" },
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

        app.MapGet("/v1/skills/{skillCode}/result", HandlePaidResourceAsync);

        return app;
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
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(
                    new ErrorBody
                    {
                        Code = "INVALID_SKILL_CODE",
                        Message = "技能编码只能包含字母、数字、下划线与连字符，且长度不超过 64。"
                    },
                    ProtocolJson.Options),
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
