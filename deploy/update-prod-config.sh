#!/usr/bin/env bash
#
# 用真实生产参数更新服务器上的 appsettings.Production.json。
#
# 设计约束：
#   1. 本脚本**不接收、不回显应用私钥的明文值**。应用私钥只来自：
#        - 配置文件中已有的值（默认原样保留）
#        - --private-key-file 指定的本地文件（读取后自动去掉 PEM 头尾与换行）
#      脚本输出只打印长度与前缀，绝不打印私钥内容。
#   2. 支付宝公钥是公开信息，可直接作为参数传入。
#   3. 切换到生产网关必须显式加 --switch-production，避免误切。
#
# 用法（在服务器上执行）：
#   bash update-prod-config.sh \
#     --app-id "2021xxxxxxxxxxxx" \
#     --alipay-public-key "MIIBIjANBgkq...AB" \
#     --seller-id "2088xxxxxxxxxxxx" \
#     --service-id "<服务市场真实 serviceId>" \
#     --seller-name "<支付账单上显示的商户名>"
#
# 正式切生产网关（签约生效 + serviceId 就位后再执行）：
#   bash update-prod-config.sh ... --switch-production
#
set -euo pipefail

CONFIG="${CONFIG:-/opt/skillpay/app/appsettings.Production.json}"
UNIT="${UNIT:-skillpay}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:18080/healthz}"

APP_ID=""
ALIPAY_PUBLIC_KEY=""
SELLER_ID=""
SERVICE_ID=""
SELLER_NAME=""
PRIVATE_KEY_FILE=""
SWITCH_PRODUCTION=0

while [ $# -gt 0 ]; do
  case "$1" in
    --app-id)            APP_ID="$2"; shift 2 ;;
    --alipay-public-key) ALIPAY_PUBLIC_KEY="$2"; shift 2 ;;
    --seller-id)         SELLER_ID="$2"; shift 2 ;;
    --service-id)        SERVICE_ID="$2"; shift 2 ;;
    --seller-name)       SELLER_NAME="$2"; shift 2 ;;
    --private-key-file)  PRIVATE_KEY_FILE="$2"; shift 2 ;;
    --switch-production) SWITCH_PRODUCTION=1; shift ;;
    *) echo "未知参数：$1" >&2; exit 2 ;;
  esac
done

[ -f "$CONFIG" ] || { echo "找不到配置文件：$CONFIG" >&2; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo "服务器缺少 python3，无法安全改写 JSON。" >&2; exit 1; }

export SP_CONFIG="$CONFIG"
export SP_APP_ID="$APP_ID"
export SP_PUBKEY="$ALIPAY_PUBLIC_KEY"
export SP_SELLER_ID="$SELLER_ID"
export SP_SERVICE_ID="$SERVICE_ID"
export SP_SELLER_NAME="$SELLER_NAME"
export SP_PRIVFILE="$PRIVATE_KEY_FILE"
export SP_SWITCH="$SWITCH_PRODUCTION"

python3 <<'PY'
import json, os, re, sys

path      = os.environ["SP_CONFIG"]
app_id    = os.environ.get("SP_APP_ID", "").strip()
pubkey    = os.environ.get("SP_PUBKEY", "").strip()
seller_id = os.environ.get("SP_SELLER_ID", "").strip()
service_id= os.environ.get("SP_SERVICE_ID", "").strip()
seller_nm = os.environ.get("SP_SELLER_NAME", "").strip()
privfile  = os.environ.get("SP_PRIVFILE", "").strip()
switch    = os.environ.get("SP_SWITCH", "0") == "1"

PROD_GATEWAY    = "https://openapi.alipay.com/gateway.do"
SANDBOX_GATEWAY = "https://openapi-sandbox.dl.alipaydev.com/gateway.do"
MOCK_SERVICE_ID = "api_mock_service_id"

with open(path, encoding="utf-8") as fh:
    doc = json.load(fh)

node = doc.setdefault("Alipay", {}).setdefault("Aipay", {})


def strip_pem(raw: str) -> str:
    body = re.sub(r"-----[A-Z ]*-----", "", raw)
    return re.sub(r"\s+", "", body)


changed, warnings = [], []

if app_id:
    if re.fullmatch(r"\d{16}", app_id):
        node["AppId"] = app_id
        changed.append("AppId")
    else:
        warnings.append(
            f"AppId 应为 16 位纯数字，收到的值不符合（长度 {len(app_id)}），已跳过。"
        )

if pubkey:
    node["AlipayPublicKey"] = strip_pem(pubkey)
    changed.append("AlipayPublicKey")
if seller_id:
    if seller_id.startswith("2088"):
        node["SellerId"] = seller_id
        changed.append("SellerId")
    else:
        warnings.append(f"SellerId 应以 2088 开头，收到的值不是，已跳过：{seller_id[:6]}…")
if seller_nm:
    node["SellerName"] = seller_nm
    changed.append("SellerName")
if service_id:
    node["ServiceId"] = service_id
    changed.append("ServiceId")

if privfile:
    if not os.path.isfile(privfile):
        sys.exit(f"私钥文件不存在：{privfile}")
    with open(privfile, encoding="utf-8") as fh:
        node["PrivateKey"] = strip_pem(fh.read())
    changed.append("PrivateKey")
    os.remove(privfile)          # 用完即删，避免明文长期留在磁盘
    warnings.append("私钥文件已读取并删除。")

if switch:
    node["ServerUrl"] = PROD_GATEWAY
    changed.append("ServerUrl→生产网关")

# 上线前校验：生产网关不得配 mock serviceId
if node.get("ServerUrl") == PROD_GATEWAY and node.get("ServiceId") == MOCK_SERVICE_ID:
    sys.exit("拒绝写入：ServerUrl 是生产网关，但 ServiceId 仍是 api_mock_service_id。")

with open(path, "w", encoding="utf-8") as fh:
    json.dump(doc, fh, ensure_ascii=False, indent=2)
    fh.write("\n")

print("已更新字段：" + (", ".join(changed) if changed else "（无）"))
for w in warnings:
    print("  注意：" + w)

priv = node.get("PrivateKey", "")
print(f"  PrivateKey 长度 = {len(priv)}（内容不显示）")
print(f"  AppId           = {node.get('AppId')}")
print(f"  ServerUrl       = {node.get('ServerUrl')}")
print(f"  ServiceId       = {node.get('ServiceId')}")
print(f"  SellerId        = {str(node.get('SellerId'))[:4]}…（共 {len(str(node.get('SellerId','')))} 位）")
print(f"  支付宝公钥长度  = {len(node.get('AlipayPublicKey',''))}")

if node.get("ServerUrl") == SANDBOX_GATEWAY:
    print("\n提醒：当前仍是沙箱网关。签约生效并拿到真实 serviceId 后，")
    print("      再加 --switch-production 重跑本脚本切到生产网关。")
PY

echo ""
echo "=== 重启服务 ==="
systemctl restart "$UNIT"
sleep 6
systemctl is-active "$UNIT"

echo "=== 启动日志（若配置非法会直接报错）==="
tail -n 12 /var/log/skillpay/app.log 2>/dev/null || echo "（暂无日志）"

echo "=== 健康检查 ==="
curl -s -o /dev/null -w "healthz HTTP %{http_code}\n" --max-time 6 "$HEALTH_URL" || echo "健康检查失败"

echo "=== 402 端点冒烟（统一入口）==="
curl -s -o /dev/null -w "result  HTTP %{http_code}\n" --max-time 6 \
  "${HEALTH_URL%/healthz}/v1/skills/result?skill_code=demo-skill" || echo "402 端点无响应"
