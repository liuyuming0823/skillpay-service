using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
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
        // ------------------------------------------------------------------
        // 快路径：订单已交付过时，不再重复验付，只校验「取货人 == 付款人」。
        //
        // 为什么必须放在验付之前：
        //   1) 支付宝对同一笔交易的**二次验付**会返回 40004 SYSTEM_ERROR，
        //      把「凭原凭据重取」这件事本身挡死（2026-09-18 实测）；
        //   2) 已交付订单的合法性在首次验付时已确认，产物就在库里，
        //      重验既无增量信息，又把重取的成功率押在支付宝的幂等行为上。
        //
        // 顺带钉死「产物按订单冻结」：这里返回的是当初落库的那一份，
        // 与 payloads/ 目录下当前放的是哪个版本完全无关。
        // ------------------------------------------------------------------
        var existing = await _orders.FindByTradeNoAsync(proof.TradeNo, cancellationToken);

        if (existing is not null
            && existing.FulfillStatus == FulfillStatus.Fulfilled
            && !string.IsNullOrWhiteSpace(existing.ServiceResult))
        {
            if (!string.Equals(existing.ResourceId, resourceId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "重取资源与订单不一致 outTradeNo={OutTradeNo} 订单资源={OrderResource} 本次资源={Requested}",
                    existing.OutTradeNo, existing.ResourceId, resourceId);

                return new PaidAccessResult
                {
                    StatusCode = StatusCodes.Status404NotFound,
                    Payload = new ErrorBody
                    {
                        Code = "RESOURCE_MISMATCH",
                        Message = "该订单对应的资源与本次请求的资源不一致。"
                    }
                };
            }

            var fastPathRejection = EvaluateClaimIdentity(existing, proof.ClientSession);

            if (fastPathRejection is not null)
            {
                return fastPathRejection;
            }

            _logger.LogInformation(
                "订单已履约，直接复用落库产出 outTradeNo={OutTradeNo} 版本={Version}（不重复验付）",
                existing.OutTradeNo,
                string.IsNullOrWhiteSpace(existing.DeliveredVersion) ? "(未记录)" : existing.DeliveredVersion);

            return Delivered(
                existing.ResourceId,
                existing.ServiceResult!,
                proof.TradeNo,
                existing.OutTradeNo,
                alreadyFulfilled: true);
        }

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

        // 一致性校验放在金额/资源校验之后、履约之前：
        // 此时才能确定订单确实属于这笔交易，判定「谁在取货」才有意义。
        var claimRejection = order is null ? null : EvaluateClaimIdentity(order, proof.ClientSession);

        if (claimRejection is not null)
        {
            return claimRejection;
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
                ClientSession = proof.ClientSession,
                PayloadVersion = definition.ResolvePayloadVersion(),
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

    /// <summary>买家会话一致性判定结果。</summary>
    private enum ClaimIdentityCheck
    {
        /// <summary>订单尚未登记会话（本特性上线前的历史订单），无从判定。</summary>
        Unbound,

        /// <summary>本次请求带来的会话与订单登记值一致。</summary>
        Matched,

        /// <summary>本次请求带了会话，但与订单登记值不同。</summary>
        Mismatch,

        /// <summary>订单已登记会话，而本次请求根本没带。</summary>
        Sessionless
    }

    /// <summary>
    /// 校验取货人与付款人是否一致，并按配置模式决定放行还是拒绝。
    /// </summary>
    /// <returns>非 <c>null</c> 表示该结果应直接作为响应返回；<c>null</c> 表示放行。</returns>
    /// <remarks>
    /// 这里是「订单号 / 交易号泄露后别人也能取货」的堵口点。
    /// 交易号与订单号都会随付款流程公开流转，只有 <c>client_session</c> 是付款人私有的那部分 ——
    /// 支付宝官方对该字段的定义即为「买家客户端会话标识，用于验证买家一致性」。
    /// <para>
    /// 未绑定会话的历史订单按「无从判定」放行，否则上线当天就会把老买家全部锁死。
    /// </para>
    /// </remarks>
    private PaidAccessResult? EvaluateClaimIdentity(OrderSnapshot order, string? requestSession)
    {
        ClaimIdentityCheck check = ClassifyClientSession(order, requestSession);

        if (check is ClaimIdentityCheck.Matched or ClaimIdentityCheck.Unbound)
        {
            return null;
        }

        string reason = check == ClaimIdentityCheck.Sessionless
            ? "请求未携带 client_session"
            : "会话标识与订单登记值不一致";

        if (ClaimIdentityModes.IsEnforce(_aipay.ClaimIdentityMode))
        {
            _logger.LogWarning(
                "拒绝取货：{Reason} outTradeNo={OutTradeNo} 订单身份={Bound} 本次身份={Incoming}",
                reason, order.OutTradeNo, DescribeSession(order.ClientSession), DescribeSession(requestSession));

            // 刻意用 403 而不是 402：402 会诱导智能体重新发起支付，让用户白白多付一笔钱。
            return new PaidAccessResult
            {
                StatusCode = StatusCodes.Status403Forbidden,
                Payload = new ErrorBody
                {
                    Code = "CLAIM_IDENTITY_MISMATCH",
                    Message = "该订单的取货凭据与当前买家身份不一致，无法交付。请用当初付款的那台客户端重取。"
                }
            };
        }

        if (ClaimIdentityModes.IsObserve(_aipay.ClaimIdentityMode))
        {
            // 观察模式：放行但留证 —— 用于判断能否安全收紧到 Enforce。
            _logger.LogWarning(
                "取货身份存疑（观察模式，仍放行）：{Reason} outTradeNo={OutTradeNo} 订单身份={Bound} 本次身份={Incoming}",
                reason, order.OutTradeNo, DescribeSession(order.ClientSession), DescribeSession(requestSession));
        }

        return null;
    }

    private static ClaimIdentityCheck ClassifyClientSession(OrderSnapshot order, string? requestSession)
    {
        if (string.IsNullOrWhiteSpace(order.ClientSession))
        {
            return ClaimIdentityCheck.Unbound;
        }

        if (string.IsNullOrWhiteSpace(requestSession))
        {
            // 订单登记过会话，而本次请求没带 —— 不因「缺参数」放行，否则只要不带这个头就能绕过校验。
            return ClaimIdentityCheck.Sessionless;
        }

        // ------------------------------------------------------------------
        // 买家侧的 client_session 是「一次性签名凭据」，形如：
        //   {"externalId":"…","signature":"…","timestamp":"…"}
        // 其中 signature 与 timestamp **每次请求都会变**，所以整串比对必然不等 ——
        // 那会把同一买家的正常重取也判成「异会话」而拒掉。
        // （2026-09-18 真机实测：一开 Enforce，付款后的重取全部 403。）
        // 跨请求真正稳定的只有 externalId，故按它比对；
        // 若两侧不是该形态（旧客户端 / 手工组装凭据），退化为整串比对以保持原语义。
        // ------------------------------------------------------------------
        string? boundIdentity = ExtractSessionIdentity(order.ClientSession);
        string? incomingIdentity = ExtractSessionIdentity(requestSession);

        if (boundIdentity is not null && incomingIdentity is not null)
        {
            return string.Equals(boundIdentity, incomingIdentity, StringComparison.Ordinal)
                ? ClaimIdentityCheck.Matched
                : ClaimIdentityCheck.Mismatch;
        }

        return string.Equals(order.ClientSession, requestSession, StringComparison.Ordinal)
            ? ClaimIdentityCheck.Matched
            : ClaimIdentityCheck.Mismatch;
    }

    /// <summary>
    /// 从 <c>client_session</c> 中取出跨请求稳定的身份子字段 <c>externalId</c>。
    /// </summary>
    /// <remarks>
    /// 该字段是 Base64URL 编码的 JSON（<c>{"externalId":…,"signature":…,"timestamp":…}</c>）。
    /// 形态不符、解码失败或取不到 <c>externalId</c> 时返回 <c>null</c>，由调用方退化为整串比对。
    /// </remarks>
    private static string? ExtractSessionIdentity(string? session)
    {
        if (!Base64Url.TryDecode(session, out string decoded))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(decoded);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("externalId", out JsonElement identity)
                && identity.ValueKind == JsonValueKind.String)
            {
                string? value = identity.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
            // 不是 JSON —— 交给整串比对。
        }

        return null;
    }

    /// <summary>会话标识只保留前 8 位用于日志；完整值属于买家凭据，不落日志。</summary>
    private static string Mask(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(空)"
        : value.Length <= 8 ? value
        : value[..8] + "…";

    /// <summary>
    /// 日志用的会话描述：优先显示稳定身份（<c>externalId</c>）前缀，取不到时退回整串前缀。
    /// 整串前缀恒为 <c>eyJleHRl…</c>（都是 base64 的 JSON 开头），没有诊断价值，故优先取身份。
    /// </summary>
    private static string DescribeSession(string? session) =>
        Mask(ExtractSessionIdentity(session) ?? session);
}
