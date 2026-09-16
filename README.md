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

数据库文件位于 `appdata/skillpay.db`（首次启动自动建表 + 建索引）。

> 路径原名 `data/`，已改为 `appdata/`。原因：Windows 文件系统不区分大小写，`data/` 与 C# 源码目录 `Data/` 是同一个文件夹，
> 既会让 gitignore 误伤源码，也会把运行期数据库写进源码目录。改名为 `appdata/` 后两者彻底错开。

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

## 七、生产部署（已完成）

### 目标环境

| 项 | 值 |
|---|---|
| 实例 | `lhins-154ixh63`（Ubuntu 22.04 LTS，广州 `ap-guangzhou-3`） |
| 公网 IP | `111.230.145.82` |
| 规格 | 2 核 2G / 50GB SSD / 4Mbps |
| 运行时 | ASP.NET Core Runtime 10（另装 SDK 10.0.401 用于编译发布） |
| 仓库 | `https://github.com/liuyuming0823/skillpay-service` |
| 代码目录 | `/opt/skillpay/src` |
| 发布目录 | `/opt/skillpay/app` |
| 日志 | `/var/log/skillpay/app.log` |

### 架构

```
公网 ──:80──> Nginx ──> 127.0.0.1:18080 ──> SkillPay.Service (systemd)
```

- `skillpay.service`：systemd 常驻，`Restart=always`，已 `enable` 开机自启
- Nginx：`/etc/nginx/sites-available/skillpay` 反代到后端，已启用（`deploy/nginx-skillpay.conf`）

### 幂等重部署

```bash
# 服务器上执行
bash /opt/skillpay/src/deploy/deploy.sh
```

脚本流程：拉取最新代码 → `dotnet publish` → 重启 systemd → 健康检查。

### 换上生产参数

拿到真实「支付宝公钥 / SellerId / serviceId」后，用 `deploy/update-prod-config.sh` 改配置，**不需要手工编辑 JSON**：

```bash
bash /opt/skillpay/src/deploy/update-prod-config.sh \
  --alipay-public-key "<平台导出的支付宝公钥>" \
  --seller-id         "<2088 开头的商户 PID>" \
  --service-id        "<服务市场真实 serviceId>" \
  --seller-name       "技能工厂"
```

签约生效、`serviceId` 就位后，再加 `--switch-production` 切到生产网关：

```bash
bash /opt/skillpay/src/deploy/update-prod-config.sh ... --switch-production
```

脚本的两条硬约束（已实测）：

| 约束 | 行为 |
|---|---|
| 生产网关 + `api_mock_service_id` | **拒绝写入**并退出，避免上线前最常见的事故 |
| 未加 `--switch-production` | 写完提示「仍是沙箱网关」，不会静默切换 |

**应用私钥处理方式**：脚本**不接受、不回显私钥明文**。默认原样保留文件中已有的值；
如需换新私钥，用 `--private-key-file <路径>` 传入文件（自动剥离 PEM 头尾与换行，读完即删）。
输出只打印长度与前缀，防止私钥进日志或终端历史。

### 公网验收结果（2026-09-16，从服务器之外的公网发起）

| 路径 | 期望 | 实测 |
|---|---|---|
| `/healthz` | 200 | ✅ 200 |
| `/v1/skills/demo-skill/result` | 402 | ✅ 402 + `Payment-Needed` 头 |
| `/v1/skills/not-there/result` | 404 | ✅ 404 |
| `/v1/skills/bad%20code/result` | 400 | ✅ 400 |

### ⚠️ 域名与 HTTPS：广州地域必须备案（实测结论）

在 80 端口使用**任意域名**（含 `111.230.145.82.sslip.io` 这类免备案解析服务）访问本实例，
会被腾讯云强制 302 拦截：

```
HTTP/1.1 302 OK
Location: https://dnspod.qcloud.com/static/webblock.html?d=111.230.145.82.sslip.io
```

同一时刻对照实验：

| 请求 | 结果 |
|---|---|
| `http://111.230.145.82/healthz`（裸 IP） | ✅ 200 |
| `http://111.230.145.82/healthz` + `Host: test.example.com` | ❌ 302 备案拦截 |
| `http://111.230.145.82.sslip.io/healthz` | ❌ 302 备案拦截 |

**结论**：广州实例上**只要请求的 Host 头带域名就会被拦，与该域名是否真实存在无关**。
因此「免备案域名」「Let's Encrypt HTTP-01 签发」在本机均不可行。
443 已放通，但**在没有已备案域名之前无法提供可信 HTTPS**。

当前可用服务地址：`http://111.230.145.82/v1/skills/{skillCode}/result`

后续若要 HTTPS，三条路：

| 方案 | 需要备案 | 代价 |
|---|---|---|
| 广州 + 自有域名 + 备案 | 需要（约 7–20 天） | 之后可用免费证书上 HTTPS |
| 中国香港地域另开实例 + 域名 | 不需要 | 需新增机器 |
| 维持 IP + HTTP | 不需要 | 明文传输，支付类审核可能存疑 |

### 待替换的占位配置

服务器 `/opt/skillpay/app/appsettings.Production.json` 目前是**占位值**：
沙箱网关地址、自签 RSA 密钥、`SellerId=2088000000000000`、`ServiceId=api_mock_service_id`。
拿到真实密钥与 `serviceId` 后需替换，**应用私钥不得提交进仓库**。

---

## 八、参考资料

- 接入行动清单：`D:/workbuddy/SkillPay-接入行动清单.md`
- 官方技能契约：`~/.workbuddy/skills/alipay-aipay/references/integration/modules/`
