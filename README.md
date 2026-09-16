# SkillPay Service

基于 **支付宝 AI 付（AIPAY）按量付费** 协议的技能支付服务。运行后对「付费技能资源」返回 HTTP **402**，
配合 `Payment-Needed` 账单头完成出价；客户端携带 `Payment-Proof` 回访时，服务端向支付宝验付并放行资源。

- 技术栈：C# / ASP.NET Core（`net10.0`）· 最小 API
- 支付宝 SDK：`AlipaySDKNet.Standard` 4.9.1369
- 持久化：EF Core + SQLite
- 签名算法：RSA2（SHA256withRSA）

---

## 一、协议实现要点

AIPAY 是**出站验证模式**：服务端主动调用支付宝接口核验支付凭据，支付宝**不会**反向回调本服务。
因此本服务**不实现** `notify_url` 异步通知——这是按量付费协议明确禁止混入的部分。

| 协议要素 | 实现位置 |
|---|---|
| HTTP 402 + `Payment-Needed` 账单头 | `Services/PaidAccessService.cs` |
| 账单 RSA2 签名（8 字段） | `Protocol/SellerSigner.cs` |
| `Payment-Proof` 解析 | `Protocol/PaymentProof.cs` |
| 验付 `alipay.aipay.agent.payment.verify` | `Payments/AlipayGateway.cs` |
| 履约确认 `alipay.aipay.agent.fulfillment.confirm` | `Payments/AlipayGateway.cs` |
| Base64URL 编解码 | `Protocol/Base64Url.cs` |
| 金额规则校验 | `Protocol/AmountRules.cs` |

签名字段（固定 8 个，字典序拼 `k=v&`）：`amount`、`currency`、`goods_name`、`out_trade_no`、
`pay_before`、`resource_id`、`seller_id`、`service_id`。

> 注意：账单 JSON 中 `goods_name` / `seller_id` / `service_id` 位于 `method` 节点下，
> 其余位于 `protocol` 节点——验签原文取值时需从两处合并，这是易错点。

---

## 二、端点

| 方法 | 路径 | 说明 | 预期状态码 |
|---|---|---|---|
| GET | `/` | 服务信息 + 已上架技能目录 | 200 |
| GET | `/healthz` | 健康检查（含数据库连通性） | 200 / 503 |
| GET | `/v1/skills/{skillCode}/result` | **付费资源端点** | 402 / 200 / 400 / 404 |

付费端点分支：

- 无 `Payment-Proof` → **402**，响应头带 `Payment-Needed`（Base64URL 编码的账单 JSON）
- `Payment-Proof` 有效 → **200**，返回技能结果
- `Payment-Proof` 无效/过期 → **402**（不返回 5xx，便于客户端重试）
- 技能编码非法（非 `[A-Za-z0-9_-]` 或超 64 字符） → **400**
- 技能未上架 → **404**

---

## 三、目录结构

```
skillpay-service/
├── Program.cs                     # 组合根：DI、配置校验、建库、端点映射
├── Configuration/
│   ├── AipayOptions.cs            # 支付宝侧配置（appId/密钥/serviceId/金额上下限）
│   └── SkillCatalogOptions.cs     # 技能目录与定价，resource_id 生成
├── Domain/
│   ├── OrderState.cs              # 订单状态枚举
│   └── OrderTypes.cs              # 领域值类型
├── Data/
│   ├── OrderRecord.cs             # 订单实体
│   ├── SkillPayDbContext.cs       # EF Core 上下文 + 索引
│   └── EfOrderRepository.cs       # 仓储实现
├── Protocol/
│   ├── Base64Url.cs               # Base64URL 编解码
│   ├── SellerSigner.cs            # RSA2 签名与验签
│   ├── AmountRules.cs             # 金额规范化与边界校验
│   ├── ProtocolContracts.cs       # 账单 / 错误体契约 + JSON 选项
│   └── PaymentProof.cs            # Payment-Proof 结构与解析
├── Payments/
│   ├── IAlipayGateway.cs          # 支付宝网关抽象
│   └── AlipayGateway.cs           # SDK 适配：verify / fulfillment.confirm
├── Services/
│   ├── PaidResourceFactory.cs     # 技能结果内容生成
│   └── PaidAccessService.cs       # 402 编排主流程
├── Endpoints/
│   └── SkillPayEndpoints.cs       # 路由映射
└── scripts/
    └── smoke-test.mjs             # 端到端联调脚本（18 项断言）
```

---

## 四、配置

`appsettings.json` 为骨架，`appsettings.Development.json` 用于本地联调。

```jsonc
{
  "Alipay": {
    "Aipay": {
      "AppId": "",                 // 支付宝应用 AppId
      "ServiceId": "",             // 签约后获得的 serviceId
      "PrivateKey": "",            // 应用私钥（PKCS#1/PKCS#8，Base64）
      "AlipayPublicKey": "",       // 支付宝公钥，用于验签
      "SellerId": "",              // 卖家唯一标识
      "Currency": "CNY",
      "GatewayUrl": "https://openapi.alipay.com/gateway.do",
      "MinAmount": "0.01",
      "MaxAmount": "1000.00"
    }
  },
  "SkillCatalog": {
    "ResourceIdPrefix": "skillpay",
    "Skills": {
      "demo-skill": {
        "Price": "0.01",
        "GoodsName": "示例技能",
        "Description": "用于联调的示例付费技能"
      }
    }
  }
}
```

> ⚠️ **密钥安全**：仓库中的开发密钥仅供 `localhost` 联调使用，**绝不能**用于生产。
> 生产密钥通过环境变量或服务器上的 `appsettings.Production.json` 注入，不进版本库。
> 启动时会做配置校验，缺失关键字段将直接失败而不是带着空值运行。

### 密钥格式要求（实测结论）

支付宝 .NET SDK 对**应用私钥**的格式非常挑剔。下表是实测结果（用真实 SDK 4.9.1369 逐个验证）：

| 私钥形态 | SDK 是否接受 | 报错 |
|---|---|---|
| 裸 Base64 + **PKCS#1** | ✅ **接受** | — |
| 裸 Base64 + PKCS#8 | ❌ 拒绝 | `RSA签名遭遇异常…不正确的长度` |
| 带 PEM 头尾（任意格式） | ❌ 拒绝 | `…The input is not a valid Base-64 string` |
| 裸 PKCS#1 + 中间夹杂换行 | ✅ 接受 | — |

**结论：必须使用 PKCS#1、且不带 PEM 头尾的原始 Base64。**

两个工程化处理：

1. **PEM 包装自动剥离** —— `AipayOptions.NormalizedPrivateKey` 在交给 SDK 前统一去掉 PEM 头尾与换行；
   配置里的原值不动。所以即使粘贴时带了 `-----BEGIN…` 也能跑。
2. **PKCS#8 启动即拦截** —— 校验器在启动阶段识别 PKCS#8 并**直接终止**，给出可照做的提示，
   而不是等到第一笔真实付款才报 `不正确的长度`。

> 生成密钥时注意：支付宝密钥工具的「生成密钥」**默认产出 PKCS#8（Java 用）**，
> C#/.NET 必须再用工具里的「**格式转换**」转成 PKCS#1。
> 应用私钥只留在本机，不要提交、不要外发。

---

## 五、本地运行

```bash
cd D:/liuyuming/GitProject/skillpay-service

# 编译
dotnet build

# 启动（Development）
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://127.0.0.1:18080"
dotnet bin/Debug/net10.0/SkillPay.Service.dll

# 另开一个终端跑联调
node scripts/smoke-test.mjs
```

数据库文件位于 `data/skillpay.db`（首次启动自动建表 + 建索引）。

### 手工验证 402

```bash
curl -i "http://127.0.0.1:18080/v1/skills/demo-skill/result"
# → HTTP 402 + Payment-Needed 头
```

---

## 六、联调结果

`scripts/smoke-test.mjs` 覆盖 **18 项断言，全部通过**：

| 分组 | 覆盖内容 |
|---|---|
| 基础可用性 | 健康检查、数据库可达、服务信息、协议标识、技能目录 |
| 402 下发 | 状态码、`Payment-Needed` 头、Base64URL 可解码 |
| 账单结构 | `protocol` / `method` 字段齐全、币种、签名类型、`seller_unique_id_key` |
| 一致性 | 响应体与账单 `out_trade_no` 一致、签名字段 8/8 有值 |
| **签名正确性** | **RSA2 独立验签通过**（用配置公钥在 Node 侧复现） |
| 时间与容错 | `pay_before` 为未来时间、无效凭据退回 402 而非 5xx |

订单持久化已验证：`skillpay_orders` 表正常写入，含 `OutTradeNo` / `Amount` / `Currency` /
`ResourceId` / `OrderStatus` / `FulfillStatus`，并建有 `TradeNo`、`ResourceId`、`FulfillStatus` 索引。

---

## 七、下一步

| 步骤 | 内容 | 前置条件 |
|---|---|---|
| 1 | 换取真实 `AppId` / `PrivateKey` / `AlipayPublicKey` / `SellerId` | 支付宝开放平台账号 |
| 2 | 签约入驻，拿到真实 `serviceId` | 上一步完成 |
| 3 | 沙箱联调（当前 Windows 环境未做沙箱验证，标记为 `VERIFY_PENDING`） | 密钥就位 |
| 4 | 部署到 Lighthouse 服务器（Ubuntu 22.04 / `111.230.145.82`） | 见下 |

### 部署待办

- **目标框架**：本机 SDK 为 .NET 10。服务器上线前需确认 .NET 10 运行时可用性，
  或改用 `net8.0`（LTS）以降低运行时风险。
- **端口与防火墙**：服务器当前仅放通 22 / 80 / ICMP，**443 未放通**，上 HTTPS 前需补规则。
- **HTTPS**：AIPAY 是出站验证模式，**不需要备案即可跑通业务逻辑**；
  若要对外提供正式访问地址，可选「自有域名 + 备案」或「Cloudflare Tunnel（免备案）」。
- **密钥注入**：生产密钥不走文件提交，用环境变量或服务器本地配置。

---

## 八、参考资料

- 接入行动清单：`D:/workbuddy/SkillPay-接入行动清单.md`
- 官方技能契约：`~/.workbuddy/skills/alipay-aipay/references/integration/modules/`
