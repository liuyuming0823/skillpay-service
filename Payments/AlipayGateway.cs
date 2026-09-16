using System.Text.Json;
using Aop.Api;
using Aop.Api.Request;
using Microsoft.Extensions.Options;
using SkillPay.Service.Configuration;
using SkillPay.Service.Protocol;

namespace SkillPay.Service.Payments;

/// <summary>
/// 基于支付宝官方 .NET SDK 的网关实现。
/// 只包含 AI 按量付费需要的两个出站接口，不涉及统一收单与异步通知。
/// </summary>
public sealed class AlipayGateway : IAlipayGateway
{
    private readonly AipayOptions _options;
    private readonly IAopClient _client;
    private readonly ILogger<AlipayGateway> _logger;

    public AlipayGateway(IOptions<AipayOptions> options, ILogger<AlipayGateway> logger)
    {
        _options = options.Value;
        _logger = logger;

        _client = new DefaultAopClient(new AlipayConfig
        {
            ServerUrl = _options.ServerUrl,
            AppId = _options.AppId,
            // SDK 只接受不带 PEM 头尾的 PKCS#1 原始 Base64，这里用规范化后的值，
            // 避免配置里带了 PEM 包装导致首次真实付款时才抛「签名异常」。
            PrivateKey = _options.NormalizedPrivateKey,
            AlipayPublicKey = _options.NormalizedAlipayPublicKey,
            Format = "json",
            Charset = "UTF-8",
            SignType = _options.SignType
        });
    }

    public bool IsExactSandboxMode => _options.IsExactSandboxMode;

    public async Task<PaymentVerifyOutcome> VerifyPaymentAsync(
        PaymentProofData proof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);

        // 注意：payment_proof 属于敏感凭据，任何情况下都不得写入日志。
        var bizContent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["payment_proof"] = proof.PaymentProof,
            ["trade_no"] = proof.TradeNo
        };

        if (!string.IsNullOrWhiteSpace(proof.ClientSession))
        {
            bizContent["client_session"] = proof.ClientSession;
        }

        try
        {
            var request = new AlipayAipayAgentPaymentVerifyRequest
            {
                BizContent = JsonSerializer.Serialize(bizContent)
            };

            var response = await Task.Run(() => _client.Execute(request), cancellationToken);

            _logger.LogInformation(
                "验付应答 tradeNo={TradeNo} code={Code} subCode={SubCode}",
                proof.TradeNo, response.Code, response.SubCode);

            return new PaymentVerifyOutcome
            {
                ApiSucceeded = string.Equals(response.Code, ResponseCodes.Success, StringComparison.Ordinal),
                Code = response.Code ?? string.Empty,
                SubCode = response.SubCode ?? string.Empty,
                SubMessage = response.SubMsg ?? string.Empty,
                Active = response.Active,
                TradeNo = response.TradeNo,
                OutTradeNo = response.OutTradeNo,
                Amount = response.Amount,
                ResourceId = response.ResourceId
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "验付调用异常 tradeNo={TradeNo}", proof.TradeNo);

            return new PaymentVerifyOutcome
            {
                ApiSucceeded = false,
                Code = ResponseCodes.TransportError,
                SubMessage = ex.Message,
                TransportFailure = true
            };
        }
    }

    public async Task<FulfillmentConfirmOutcome> ConfirmFulfillmentAsync(
        string tradeNo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tradeNo))
        {
            return new FulfillmentConfirmOutcome
            {
                ApiSucceeded = false,
                Code = ResponseCodes.InvalidArgument,
                SubMessage = "tradeNo 为空，无法发起履约确认"
            };
        }

        try
        {
            var request = new AlipayAipayAgentFulfillmentConfirmRequest
            {
                BizContent = JsonSerializer.Serialize(new { trade_no = tradeNo })
            };

            var response = await Task.Run(() => _client.Execute(request), cancellationToken);

            _logger.LogInformation(
                "履约确认应答 tradeNo={TradeNo} code={Code} subCode={SubCode}",
                tradeNo, response.Code, response.SubCode);

            return new FulfillmentConfirmOutcome
            {
                ApiSucceeded = string.Equals(response.Code, ResponseCodes.Success, StringComparison.Ordinal),
                Code = response.Code ?? string.Empty,
                SubCode = response.SubCode ?? string.Empty,
                SubMessage = response.SubMsg ?? string.Empty
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "履约确认调用异常 tradeNo={TradeNo}", tradeNo);

            return new FulfillmentConfirmOutcome
            {
                ApiSucceeded = false,
                Code = ResponseCodes.TransportError,
                SubMessage = ex.Message,
                TransportFailure = true
            };
        }
    }

    /// <summary>支付宝应答码常量。</summary>
    private static class ResponseCodes
    {
        /// <summary>接口调用成功。</summary>
        public const string Success = "10000";

        /// <summary>入参非法。</summary>
        public const string InvalidArgument = "INVALID_ARGUMENT";

        /// <summary>本地定义的网络/解析异常标记，非支付宝返回码。</summary>
        public const string TransportError = "TRANSPORT_ERROR";
    }
}
