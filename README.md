# SkillPay Service

自托管的**支付宝 AI 付（AIPAY）按量付费**服务端。

对「付费资源」请求返回 HTTP **402** 与 `Payment-Needed` 账单头；调用方完成支付后携带
`Payment-Proof` 回访，服务端向支付宝验付、落库订单，然后交付资源。

- 技术栈：C# / ASP.NET Core（`net10.0`）· 最小 API
- 支付宝 SDK：`AlipaySDKNet.Standard` 4.9.1369
- 持久化：EF Core + SQLite（单文件，不需要额外数据库服务）
- 签名算法：RSA2（SHA256withRSA）
- 交付形态：文本交付 / 文件交付（Base64 内联）
- 运行时依赖：只需 .NET 运行时；不需要 Redis、消息队列

---

## 一、它解决什么问题

智能体（Agent）要按次付费调用你的东西时，需要一套「报价 → 收款 → 交付」的服务端。
这套服务把协议部分全部实现好了，你只需要做两件事：

1. 在 `appsettings.json` 里登记你要卖什么、卖多少钱；
2. 把要交付的东西（一个文件，或一段文本）放到 `payloads/` 下。

协议、签名、订单、验付、幂等履约、并发去重都由服务端处理。

---

## 二、协议实现要点

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

### 签名字段与取值位置（易错点）

签名原文是**固定 8 个字段**按字典序拼接的 `k=v&`：

```
amount=...&currency=...&goods_name=...&out_trade_no=...&pay_before=...&resource_id=...&seller_id=...&service_id=...
```

> ⚠️ 取值位置不在一处：`goods_name` / `seller_id` / `service_id` 在账单 JSON 的 **`method`** 节点下，
> 其余 5 个在 **`protocol`** 节点下。只从 `protocol` 取值会导致签名验证失败。

### 交付内容的位置（同样易错）

验付成功后的 200 响应体形如：

```json
{
  "resource_id": "skillpay:my-product",
  "content": "{\"status\":\"success\",\"delivery\":{...}}",
  "trade_no": "2026...",
  "out_trade_no": "SP...",
  "already_fulfilled": false,
  "fulfillment_confirmed": true
}
```

> ⚠️ `content` 是一个 **JSON 字符串**，不是对象。客户端需要先 `JSON.parse(content)`，
> 再从 `delivery` 里取交付物。这是刻意设计：交付内容形态各异，用字符串承载可以不影响协议层。

---

## 三、端点

| 方法 | 路径 | 说明 | 预期状态码 |
|---|---|---|---|
| GET | `/` | 服务信息 + 已上架资源目录 | 200 |
| GET | `/healthz` | 健康检查（含数据库连通性） | 200 / 503 |
| POST / GET | `/v1/skills/result` | **统一付费入口**（资源编码在 body 或 query） | 402 / 200 / 400 / 404 |
| GET | `/v1/skills/{skillCode}/result` | 路径式付费入口（资源编码在地址里） | 402 / 200 / 400 / 404 |

付费端点分支：

- 无 `Payment-Proof` → **402**，响应头带 `Payment-Needed`（Base64URL 编码的账单 JSON）
- `Payment-Proof` 有效 → **200**，返回交付内容
- `Payment-Proof` 无效/过期 → **402**（不返回 5xx，便于客户端重试）
- 资源编码非法（非 `[A-Za-z0-9_-]` 或超 64 字符） → **400**
- 资源未上架 → **404**

> 在支付宝服务市场登记「服务地址」时，请填**统一入口** `/v1/skills/result`。
> 这样以后新增资源只改配置，不需要再去平台改地址。

---

## 四、目录结构

```
skillpay-service/
├── Program.cs                     # 组合根：DI、配置校验、建库、端点映射
├── Configuration/
│   ├── AipayOptions.cs            # 支付宝侧配置 + 启动期校验（含密钥格式检查）
│   ├── SkillCatalogOptions.cs     # 可售资源目录与定价
│   └── DeliveryOptions.cs         # 交付配置（目录、时限、体积上限）
├── Domain/
│   ├── OrderState.cs              # 订单状态枚举
│   └── OrderTypes.cs              # 领域值类型与仓储端口
├── Data/
│   ├── OrderRecord.cs             # 订单实体
│   ├── SkillPayDbContext.cs       # EF Core 上下文 + 索引
│   └── EfOrderRepository.cs       # 仓储实现（幂等履约）
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
│   ├── PaidResourceFactory.cs     # 交付物生成（文本 / 文件 / 占位）
│   └── PaidAccessService.cs       # 402 编排主流程
├── Endpoints/
│   └── SkillPayEndpoints.cs       # 路由映射
├── payloads/                      # 交付文件目录（放你要卖的东西）
├── deploy/                        # systemd / nginx / 部署与配置脚本
└── scripts/
    └── smoke-test.mjs             # 端到端联调脚本（18 项断言）
```

---

## 五、配置

配置分三块，都在 `appsettings.json`（生产环境用 `appsettings.Production.json` 覆盖）。

### 5.1 支付宝侧 `Alipay:Aipay`

```jsonc
{
  "Alipay": {
    "Aipay": {
      "ServerUrl": "https://openapi-sandbox.dl.alipaydev.com/gateway.do", // 沙箱网关
      "AppId": "2021xxxxxxxxxxxx",      // 开放平台应用 APPID，16 位数字
      "PrivateKey": "<应用私钥 PKCS#1 纯 Base64>",
      "AlipayPublicKey": "<支付宝公钥>",
      "SellerId": "2088xxxxxxxxxxxx",   // 商户 PID
      "SellerName": "<账单商户名>",
      "ServiceId": "<服务市场 serviceId>",
      "Currency": "CNY",
      "SignType": "RSA2",
      "SellerUniqueIdKey": "seller_id",
      "PayWindowMinutes": 30
    }
  }
}
```

启动时会做**严格校验**，缺失或格式错误直接终止进程（而不是带着空值运行）：

| 校验项 | 行为 |
|---|---|
| `ServerUrl` 非法 | 启动失败 |
| 六个关键字段为空 | 启动失败，逐个列出 |
| `SellerId` 不以 `2088` 开头 | 启动失败 |
| `PrivateKey` 是 PKCS#8（而非 PKCS#1） | 启动失败，并提示做格式转换 |
| 生产网关 + 仍是 `api_mock_service_id` | 启动失败（上线前最常见事故） |
| `PayWindowMinutes` 不在 1–1440 | 启动失败 |

全部配置项都可用**环境变量**覆盖（优先级高于配置文件，便于服务器上不落盘密钥）：

`ALIPAY_GATEWAY`、`ALIPAY_APP_ID`、`ALIPAY_SELLER_ID`、`ALIPAY_SELLER_NAME`、
`ALIPAY_SERVICE_ID`、`ALIPAY_PRIVATE_KEY`、`ALIPAY_PUBLIC_KEY`

### 5.2 资源目录 `SkillCatalog`

```jsonc
{
  "SkillCatalog": {
    "ResourceIdPrefix": "skillpay",
    "Skills": {
      "my-product": {
        "Price": "29.90",
        "GoodsName": "我的源码包",
        "Description": "一句话说明，会出现在服务信息接口里",
        "PayloadFile": "my-source.zip"
      }
    }
  }
}
```

| 字段 | 说明 |
|---|---|
| key（如 `my-product`） | **资源编码**，即请求里的 `skill_code`。只能用小写字母、数字、连字符、下划线 |
| `Price` | 单价（元），最多两位小数。**必须与支付宝服务市场登记的「服务单价」完全一致**，否则用户无法支付 |
| `GoodsName` | 账单商品名，用户在自己支付宝里看到的就是它 |
| `Description` | 仅出现在 `GET /` 的目录里，不参与支付 |

### 5.3 交付配置 `Delivery`

```jsonc
{
  "Delivery": {
    "FulfillmentTimeoutSeconds": 600,   // 履约时间预算（秒）
    "PayloadRoot": "payloads",          // 交付文件根目录
    "MaxInlinePayloadBytes": 8388608    // 单次内联交付上限（字节），默认 8 MiB
  }
}
```

`FulfillmentTimeoutSeconds` 说明：**履约一旦开始就与买家的 HTTP 连接脱钩**——
客户端断开不会取消交付，服务端会把活干完并落库。这个值是它的独立时间上限。
如果你要现场生成内容（例如调用大模型），把它调大。

---

## 六、配置交付物

两种交付形态，二选一（同时配置时**文件优先**）。

### 6.1 文本交付 `PayloadText`

适合授权码、简短说明、提示词等小内容：

```jsonc
"license-key": {
  "Price": "1.00",
  "GoodsName": "授权码",
  "PayloadText": "你的授权码是 ABC-123-XYZ"
}
```

产出：

```json
{
  "status": "success",
  "delivery": {
    "type": "text",
    "file_name": "license-key.txt",
    "mime_type": "text/plain; charset=utf-8",
    "size_bytes": 42,
    "sha256": "…",
    "content": "你的授权码是 ABC-123-XYZ"
  }
}
```

### 6.2 文件交付 `PayloadFile`

适合源码包、安装包、数据集、报告。把文件放进 `payloads/`，然后登记：

```jsonc
"my-source": {
  "Price": "29.90",
  "GoodsName": "我的源码包",
  "PayloadFile": "my-source.zip",
  "PayloadFileName": "my-source.zip",       // 可选，对外展示的文件名
  "PayloadMimeType": "application/zip"      // 可选，缺省按扩展名推断
}
```

产出：

```json
{
  "status": "success",
  "delivery": {
    "type": "file_base64",
    "file_name": "my-source.zip",
    "mime_type": "application/zip",
    "size_bytes": 1048576,
    "sha256": "e3b0c44298fc1c149afbf4c8996fb924...",
    "content_base64": "UEsDBBQAAAAIA..."
  }
}
```

客户端拿到后：

```js
const inner = JSON.parse(response.content);
const { file_name, content_base64, sha256 } = inner.delivery;
const buf = Buffer.from(content_base64, 'base64');
fs.writeFileSync(file_name, buf);

// 务必校验摘要，确认传输完整
const actual = crypto.createHash('sha256').update(buf).digest('hex');
if (actual !== sha256) throw new Error('交付文件校验失败');
```

**两条硬约束**：

- `PayloadFile` 只允许指向 `Delivery:PayloadRoot` 目录内的文件（有路径穿越防护）。
- 单文件超过 `MaxInlinePayloadBytes`（默认 8 MiB）会**明确报错**，而不是静默截断。
  Base64 会让体积膨胀约 1/3，更大的交付物建议放到对象存储，然后改用 `PayloadText`
  返回一个带时限的下载地址。

### 6.3 两者都不配 = 占位产出

只用于打通链路与协议自测。启动日志会对这类资源发 `LogWarning`。

---

## 七、应用私钥的格式要求（实测结论）

支付宝 .NET SDK 对**应用私钥**的格式非常挑剔。下表是用真实 SDK 4.9.1369 逐个验证的结果：

| 私钥形态 | SDK 是否接受 | 报错 |
|---|---|---|
| 裸 Base64 + **PKCS#1** | ✅ **接受** | — |
| 裸 Base64 + PKCS#8 | ❌ 拒绝 | `RSA签名遭遇异常…不正确的长度` |
| 带 PEM 头尾（任意格式） | ❌ 拒绝 | `…The input is not a valid Base-64 string` |
| 裸 PKCS#1 + 中间夹杂换行 | ✅ 接受 | — |

**结论：必须使用 PKCS#1、且不带 PEM 头尾的原始 Base64。**

服务端已做两个工程化处理，降低踩坑成本：

1. **PEM 包装自动剥离** —— 交给 SDK 前统一去掉 PEM 头尾与换行，配置原值不动。
   所以即使粘贴时带了 `-----BEGIN…` 也能跑。
2. **PKCS#8 启动即拦截** —— 启动阶段识别 PKCS#8 并**直接终止**，给出可照做的提示，
   而不是等到第一笔真实付款才报 `不正确的长度`。

> 生成密钥时注意：支付宝密钥工具的「生成密钥」**默认产出 PKCS#8（Java 用）**，
> C#/.NET 必须再用工具里的「**格式转换**」转成 PKCS#1。

---

## 八、本地运行

```bash
# 编译
dotnet build

# 启动（Development 用沙箱配置）
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://127.0.0.1:18080"
dotnet bin/Debug/net10.0/SkillPay.Service.dll

# 另开一个终端跑联调（18 项断言）
node scripts/smoke-test.mjs
```

数据库文件位于 `appdata/skillpay.db`（首次启动自动建表 + 建索引）。

> 目录名为什么是 `appdata/` 而不是 `data/`：Windows 文件系统不区分大小写，
> `data/` 与 C# 源码目录 `Data/` 是同一个文件夹，既会让 gitignore 误伤源码，
> 也会把运行期数据库写进源码目录。改名后两者彻底错开。

### 手工验证 402

```bash
curl -i "http://127.0.0.1:18080/v1/skills/demo-skill/result"
# → HTTP 402 + Payment-Needed 头
```

把响应头里的 `Payment-Needed` 值做 Base64URL 解码，就能看到完整账单：

```bash
curl -s -D - -o /dev/null "http://127.0.0.1:18080/v1/skills/demo-skill/result" \
  | grep -i '^payment-needed' | cut -d' ' -f2 \
  | tr '_-' '/+' | base64 -d 2>/dev/null | python3 -m json.tool
```

---

## 九、部署

详见 `docs/接入教程.md`。要点：

```
公网 ──:80──> Nginx ──> 127.0.0.1:18080 ──> SkillPay.Service (systemd)
```

- `deploy/skillpay.service`：systemd 常驻，`Restart=always`
- `deploy/nginx-skillpay.conf`：反向代理配置
- `deploy/deploy.sh`：幂等重部署（拉代码 → 发布 → 重启 → 健康检查）
- `deploy/update-prod-config.sh`：安全改写生产配置，**不回显私钥**

> `deploy.sh` 会 `rm -rf` 应用目录再重新发布，因此它**显式暂存并恢复两个东西**：
> `appsettings.Production.json` 和 `appdata/`（订单库）。
> 订单库被清空会导致已付款订单查不到、用户付了钱拿不到货，这个坑已经在脚本里堵住。

---

## 十、故障排查

| 现象 | 原因 | 处理 |
|---|---|---|
| 启动即退出，报 `PrivateKey 是 PKCS#8 格式` | 私钥格式不对 | 用密钥工具「格式转换」转 PKCS#1 |
| 启动即退出，报 `ServiceId 未配置` | 生产网关配了 mock serviceId | 填入服务市场真实 serviceId |
| `RSA签名遭遇异常…不正确的长度` | 私钥被 PEM 头尾包裹或含换行 | 代码已自动剥离，检查是否混入了说明文字 |
| 用户扫码后提示「金额不符」 | 目录 `Price` 与平台登记单价不一致 | 两边改成完全一致 |
| 付款成功但返回 402 | 本地订单校验未通过 | 查日志里 `本地订单校验未通过` 那行的三个布尔值 |
| 付款成功但交付失败 | 交付文件不存在 / 超过内联上限 | 查日志；确认 `payloads/` 下文件确实存在 |
| 客户端超时断开 | 交付耗时超过客户端耐心 | 履约已与连接脱钩，**用同一 `Payment-Proof` 重试即可取回** |
| 重复请求同一次交付 | 幂等设计 | 用同一 `Payment-Proof` 重试会复用已有产出，不会重复扣费 |

日志里几个关键行（便于定位）：

```
已下发 402 账单 outTradeNo=... amount=... resourceId=...
验付应答 tradeNo=... code=10000
本地订单校验未通过 outTradeNo=... amountMatches=... resourceMatches=... orderUsable=...
履约准备失败 outTradeNo=...
```

---

## 十一、安全须知

- **应用私钥不得进版本库**。`.gitignore` 已忽略 `appsettings.Production.json`、`*.pem`、`*.key` 等。
- 生产环境推荐用**环境变量**注入私钥，而不是写进配置文件。
- 支付宝**公钥**是公开信息，用于校验支付宝应答签名，可以正常保存。
- `payloads/` 目录里放的是你要卖的东西——**部署时不要把它放进公开可访问的静态目录**，
  只能通过付费端点交付。
- 交付文件读取有路径穿越防护（必须落在 `PayloadRoot` 内）。

---

## 十二、域名与 HTTPS

在中国大陆地域的云服务器上，用**任意域名**（含免备案解析服务）访问 80 端口会被云厂商
强制 302 拦截到备案提示页，**与该域名是否真实存在无关**。实测对照：

| 请求 | 结果 |
|---|---|
| `http://<公网IP>/healthz`（裸 IP） | ✅ 200 |
| `http://<公网IP>/healthz` + `Host: test.example.com` | ❌ 302 备案拦截 |
| `http://<公网IP>.sslip.io/healthz` | ❌ 302 备案拦截 |

**结论**：裸 IP + HTTP 可直接用；要上 HTTPS 必须先有**已备案域名**。三条路：

| 方案 | 需要备案 | 代价 |
|---|---|---|
| 大陆地域 + 自有域名 + 备案 | 需要（约 7–20 天） | 之后可用免费证书上 HTTPS |
| 中国香港/境外地域另开实例 + 域名 | 不需要 | 需新增机器 |
| 维持 IP + HTTP | 不需要 | 明文传输，支付类审核可能存疑 |

`deploy/nginx-skillpay.conf` 里已经准备好 HTTPS 配置块（注释状态），拿到证书后取消注释即可。

---

## 十三、联调覆盖

`scripts/smoke-test.mjs` 覆盖 **18 项断言**：

| 分组 | 覆盖内容 |
|---|---|
| 基础可用性 | 健康检查、数据库可达、服务信息、协议标识、资源目录 |
| 402 下发 | 状态码、`Payment-Needed` 头、Base64URL 可解码 |
| 账单结构 | `protocol` / `method` 字段齐全、币种、签名类型、`seller_unique_id_key` |
| 一致性 | 响应体与账单 `out_trade_no` 一致、签名字段 8/8 有值 |
| **签名正确性** | **RSA2 独立验签通过**（用配置公钥在 Node 侧复现） |
| 时间与容错 | `pay_before` 为未来时间、无效凭据退回 402 而非 5xx |
