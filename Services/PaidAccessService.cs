using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SkillPay.Service.Configuration;
using SkillPay.Service.Domain;
using SkillPay.Service.Payments;
using SkillPay.Service.Protocol;

namespace SkillPay.Service.Services;

/// <summary>一次付费访问请求的处理结果。由端点层写入 HTTP 响应。</summary>
public sealed record PaidAccessResult
{
    public required int StatusCode { get; init; }

    public required object Payload { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(0);
}

/// <summary>
/// AI 按量付费（402）核心流程：下发账单、严格验付、幂等履约。
/// </summary>
/// <remarks>
/// 本流程只实现 HTTP 402、<c>Payment-Needed</c>、<c>Payment-Proof</c>、
/// <c>alipay.aipay.agent.payment.verify</c> 与 <c>alipay.aipay.agent.fulfillment.confirm</c>，
/// 不含统一收单接口、异步通知或 <c>notify_url</c>。
/// </remarks>
public sealed class PaidAccessService
{
    private readonly IOrderRepository _orders;
    private readonly IAlipayGateway _gateway;
    private readonly AipayOptions _aipay;
    private readonly SkillCatalogOptions _catalog;
    private readonly DeliveryOptions _delivery;
    private readonly PaidResourceFactory _resources;
    private readonly TimeProvider _time;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PaidAccessService> _logger;

    public PaidAccessService(
        IOrderRepository orders,
        IAlipayGateway gateway,
        IOptions<AipayOptions> aipay,
        IOptions<SkillCatalogOptions> catalog,
        IOptions<DeliveryOptions> delivery,
        PaidResourceFactory resources,
        TimeProvider time,
        IHostApplicationLifetime lifetime,
        ILogger<PaidAccessService> logger)
    {
        _orders = orders;
        _gateway = gateway;
        _aipay = aipay.Value;
        _catalog = catalog.Value;
        _delivery = delivery.Value;
        _resources = resources;
        _time = time;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>处理一次付费资源请求。</summary>
    /// <param name="skillCode">资源编码。</param>
    /// <param name="paymentProofHeader"><c>Payment-Proof</c> 头；为空时下发 402 账单。</param>
    /// <param name="inputJson">
    /// 原始请求体，透传给交付逻辑读取参数。
    /// 账单本身不携带它，因此调用方携带 <c>Payment-Proof</c> 重试时必须**原样重发同一请求体**，
    /// 否则会出现「付 A 的钱、拿 B 的货」。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<PaidAccessResult> ExecuteAsync(
        string skillCode,
        string? paymentProofHeader,
        string? inputJson = null,
        CancellationToken cancellationToken = default)
    {
        var definition = _catalog.Resolve(skillCode);
        if (definition is null)
        {
            _logger.LogInformation("请求了未上架的技能 skillCode={SkillCode}", skillCode);

            return new PaidAccessResult
            {
                StatusCode = StatusCodes.Status404NotFound,
                Payload = new ErrorBody
                {
                    Code = "SKILL_NOT_FOUND",
                    Message = $"技能 {skillCode} 未在服务端上架。"
                }
            };
        }

        var resourceId = _catalog.BuildResourceId(skillCode);

        if (string.IsNullOrWhiteSpace(paymentProofHeader))
        {
            return await IssuePaymentRequiredAsync(definition, resourceId, cancellationToken);
        }

        if (!PaymentProofReader.TryRead(paymentProofHeader, out var proof, out var error))
        {
            _logger.LogWarning("Payment-Proof 解析失败，重新下发账单：{Error}", error);
            return await IssuePaymentRequiredAsync(definition, resourceId, cancellationToken);
        }

        return await VerifyAndFulfillAsync(skillCode, definition, resourceId, proof!, inputJson, cancellationToken);
    }

    /// <summary>
    /// 下发 402 账单。必须在持久化待支付订单成功之后才返回，
    /// 否则用户付款后服务端将无法对账。
    /// </summary>
    private async Task<PaidAccessResult> IssuePaymentRequiredAsync(
        SkillDefinition definition,
        string resourceId,
        CancellationToken cancellationToken)
    {
        string outTradeNo = NewOutTradeNo();
        string amount = definition.NormalizedPrice();
        string currency = _aipay.Currency;
        string goodsName = definition.GoodsName;
        string payBefore = _time.GetLocalNow()
            .AddMinutes(_aipay.PayWindowMinutes)
            .ToString("o", CultureInfo.InvariantCulture);

        var signParameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount"] = amount,
            ["currency"] = currency,
            ["goods_name"] = goodsName,
            ["out_trade_no"] = outTradeNo,
            ["pay_before"] = payBefore,
            ["resource_id"] = resourceId,
            ["seller_id"] = _aipay.SellerId,
            ["service_id"] = _aipay.ServiceId
        };

        string sellerSignature = SellerSigner.Sign(signParameters, _aipay.PrivateKey);

        try
        {
            await _orders.CreatePendingAsync(
                new OrderSnapshot
                {
                    OutTradeNo = outTradeNo,
                    Amount = amount,
                    Currency = currency,
                    ResourceId = resourceId,
                    GoodsName = goodsName,
                    PayBefore = payBefore,
                    OrderStatus = OrderStatus.PendingPayment,
                    FulfillStatus = FulfillStatus.Unfulfilled
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建待支付订单失败 outTradeNo={OutTradeNo}", outTradeNo);

            return new PaidAccessResult
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                Payload = new ErrorBody
                {
                    Code = "CREATE_ORDER_ERROR",
                    Message = "创建订单失败，请稍后重试。"
                }
            };
        }

        var envelope = new PaymentNeededEnvelope
        {
            Protocol = new PaymentNeededProtocol
            {
                OutTradeNo = outTradeNo,
                Amount = amount,
                Currency = currency,
                ResourceId = resourceId,
                PayBefore = payBefore,
                SellerSignature = sellerSignature,
                SellerSignType = _aipay.SignType,
                SellerUniqueId = _aipay.SellerId
            },
            Method = new PaymentNeededMethod
            {
                SellerName = _aipay.SellerName,
                SellerId = _aipay.SellerId,
                SellerAppId = _aipay.AppId,
                GoodsName = goodsName,
                SellerUniqueIdKey = _aipay.SellerUniqueIdKey,
                ServiceId = _aipay.ServiceId
            }
        };

        _logger.LogInformation(
            "已下发 402 账单 outTradeNo={OutTradeNo} amount={Amount} resourceId={ResourceId}",
            outTradeNo, amount, resourceId);

        return new PaidAccessResult
        {
            StatusCode = StatusCodes.Status402PaymentRequired,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Payment-Needed"] = envelope.ToBase64Url()
            },
            Payload = new PaymentRequiredBody
            {
                Message = "需要支付",
                OutTradeNo = outTradeNo,
                Amount = amount,
                Currency = currency,
                GoodsName = goodsName
            }
        };
    }

    /// <summary>
    /// 严格验付后交付资源。任一校验失败都退回 402，让智能体重新发起支付。
    /// </summary>
    private async Task<PaidAccessResult> VerifyAndFulfillAsync(
        string skillCode,
        SkillDefinition definition,
        string resourceId,
        PaymentProofData proof,
        string? inputJson,
        CancellationToken cancellationToken)
    {
        var outcome = await _gateway.VerifyPaymentAsync(proof, cancellationToken);

        if (!outcome.ApiSucceeded)
        {
            _logger.LogWarning(
                "验付未通过 tradeNo={TradeNo} code={Code} subCode={SubCode} transport={Transport}",
                proof.TradeNo, outcome.Code, outcome.SubCode, outcome.TransportFailure);

            return await IssuePaymentRequiredAsync(definition, resourceId, cancellationToken);
        }

        var order = string.IsNullOrWhiteSpace(outcome.OutTradeNo)
            ? null
            : await _orders.FindByOutTradeNoAsync(outcome.OutTradeNo!, cancellationToken);
        // 沙箱应答可能省略部分字段，此时按本地订单回填后再做一致性校验。
        bool sandbox = _gateway.IsExactSandboxMode;

        string verifyTradeNo = !string.IsNullOrWhiteSpace(outcome.TradeNo)
            ? outcome.TradeNo!
            : sandbox ? proof.TradeNo : string.Empty;

        string verifyAmount = !string.IsNullOrWhiteSpace(outcome.Amount)
            ? outcome.Amount!
            : sandbox && order is not null ? order.Amount : string.Empty;

        string verifiedResourceId = !string.IsNullOrWhiteSpace(outcome.ResourceId)
            ? outcome.ResourceId!
            : sandbox && order is not null ? order.ResourceId : string.Empty;

        if (!outcome.Active
            || string.IsNullOrWhiteSpace(verifyTradeNo)
            || !string.Equals(verifyTradeNo, proof.TradeNo, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(outcome.OutTradeNo)
            || string.IsNullOrWhiteSpace(verifiedResourceId))
        {
            _logger.LogWarning(
                "验付结果缺少必要字段 tradeNo={TradeNo} active={Active} outTradeNo={OutTradeNo}",
                verifyTradeNo, outcome.Active, outcome.OutTradeNo);

            return await IssuePaymentRequiredAsync(definition, resourceId, cancellationToken);
        }

        bool amountMatches = order is not null && AmountRules.AreEqual(order.Amount, verifyAmount);

        bool resourceMatches = order is not null
            && string.Equals(order.ResourceId, resourceId, StringComparison.Ordinal)
            && string.Equals(order.ResourceId, verifiedResourceId, StringComparison.Ordinal);

        bool fulfillmentInProgress = order is not null
            && order.FulfillStatus is FulfillStatus.PendingConfirm or FulfillStatus.Fulfilled;

        bool orderUsable = order is not null
            && string.Equals(order.Currency, _aipay.Currency, StringComparison.Ordinal)
            && order.OrderStatus is OrderStatus.PendingPayment or OrderStatus.Paid
            && order.FulfillStatus is FulfillStatus.Unfulfilled or FulfillStatus.PendingConfirm or FulfillStatus.Fulfilled
            // 已进入履约的订单不再受支付截止时间约束，避免已付款订单被重复扣费。
            && (fulfillmentInProgress || IsFuture(order.PayBefore));

        if (!amountMatches || !resourceMatches || !orderUsable)
        {
            _logger.LogWarning(
                "本地订单校验未通过 outTradeNo={OutTradeNo} amountMatches={AmountMatches} " +
                "resourceMatches={ResourceMatches} orderUsable={OrderUsable}",
                outcome.OutTradeNo, amountMatches, resourceMatches, orderUsable);

            return await IssuePaymentRequiredAsync(definition, resourceId, cancellationToken);
        }

        var outTradeNo = outcome.OutTradeNo!;

        // 关键：履约一旦开始，就与买家的 HTTP 连接脱钩。
        // 交付可能耗时数分钟（例如现场生成内容、打包大文件），而买家客户端
        // （或中间的反向代理）往往等不了那么久。
        // 若继续沿用 RequestAborted，客户端一断开，交付随之被取消、订单永远停在「未履约」——
        // 结果是用户付了钱却拿不到东西，且重试也不会变好。
        // 所以这里换成「应用关停 + 独立时间预算」的令牌：客户端走了，活也要干完并落库。
        using var fulfillmentCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.ApplicationStopping);

        fulfillmentCts.CancelAfter(
            TimeSpan.FromSeconds(Math.Max(30, _delivery.FulfillmentTimeoutSeconds)));

        CancellationToken fulfillToken = fulfillmentCts.Token;

        var preparation = await _orders.PrepareFulfillmentAsync(
            new FulfillmentRequest
            {
                OutTradeNo = outTradeNo,
                TradeNo = verifyTradeNo,
                ExpectedAmount = AmountRules.Normalize(verifyAmount),
                ExpectedResourceId = verifiedResourceId,
                CreateResourceAsync = token =>
                    _resources.CreateAsync(resourceId, outTradeNo, skillCode, inputJson, token)
            },
            fulfillToken);

        if (preparation is null
            || (preparation.State != FulfillStatus.PendingConfirm && preparation.State != FulfillStatus.Fulfilled)
            || string.IsNullOrWhiteSpace(preparation.ServiceResult))
        {
            _logger.LogError("履约准备失败 outTradeNo={OutTradeNo}", outTradeNo);

            return new PaidAccessResult
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                Payload = new ErrorBody
                {
                    Code = "FULFILLMENT_PREPARE_FAILED",
                    Message = "订单状态不允许履约，请勿重复支付。"
                }
            };
        }

        // 已确认过履约的订单直接复用既有结果，不重复调用支付宝。
        if (preparation.State == FulfillStatus.Fulfilled)
        {
            return Delivered(resourceId, preparation.ServiceResult, verifyTradeNo, outTradeNo, alreadyFulfilled: true);
        }

        var confirm = await _gateway.ConfirmFulfillmentAsync(verifyTradeNo, fulfillToken);

        if (!confirm.ApiSucceeded)
        {
            _logger.LogWarning(
                "履约确认失败 outTradeNo={OutTradeNo} tradeNo={TradeNo} code={Code} subCode={SubCode}",
                outTradeNo, verifyTradeNo, confirm.Code, confirm.SubCode);

            // 资源已生成并落库，允许用同一 Payment-Proof 重试确认。
            return new PaidAccessResult
            {
                StatusCode = StatusCodes.Status502BadGateway,
                Payload = new ErrorBody
                {
                    Code = "FULFILLMENT_CONFIRM_FAILED",
                    Message = "资源已生成但履约确认失败，请稍后使用同一 Payment-Proof 重试。"
                }
            };
        }

        await _orders.MarkFulfilledAsync(outTradeNo, verifyTradeNo, fulfillToken);

        return Delivered(resourceId, preparation.ServiceResult, verifyTradeNo, outTradeNo, alreadyFulfilled: false);
    }

    private static PaidAccessResult Delivered(
        string resourceId,
        string content,
        string tradeNo,
        string outTradeNo,
        bool alreadyFulfilled)
    {
        var receipt = new PaymentValidationReceipt
        {
            TradeNo = tradeNo,
            OutTradeNo = outTradeNo,
            ResourceId = resourceId,
            Validated = true
        };

        return new PaidAccessResult
        {
            StatusCode = StatusCodes.Status200OK,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Payment-Validation"] = Base64Url.Encode(
                    System.Text.Json.JsonSerializer.Serialize(receipt, ProtocolJson.Options))
            },
            Payload = new ResourceDeliveredBody
            {
                ResourceId = resourceId,
                Content = content,
                TradeNo = tradeNo,
                OutTradeNo = outTradeNo,
                AlreadyFulfilled = alreadyFulfilled,
                FulfillmentConfirmed = true
            }
        };
    }

    private bool IsFuture(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var deadline)
        && deadline > _time.GetUtcNow();

    /// <summary>
    /// 生成商户订单号：<c>SP</c> + 毫秒时间戳 + 48 位随机数，长度 31，满足支付宝 64 字符上限。
    /// </summary>
    private string NewOutTradeNo() =>
        $"SP{_time.GetUtcNow():yyyyMMddHHmmssfff}{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))}";
}
