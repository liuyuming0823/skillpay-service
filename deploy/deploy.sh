#!/usr/bin/env bash
#
# SkillPay 上线 / 更新脚本（在服务器上执行，幂等可重复运行）
#
#   bash /opt/skillpay/src/deploy/deploy.sh
#
# 做四件事：拉最新代码 → 发布到 /opt/skillpay/app → 安装 systemd 单元 → 健康检查
# 注意：会保留 appsettings.Production.json（生产密钥不在仓库里，必须原地保留）
#
# ⚠️ 部署分支 = release/source-v1.0.0（交付版），**不是 main**。
#    main 是「LLM 技能生成版」：它没有 Configuration/DeliveryOptions.cs，
#    PaidResourceFactory 也不认 PayloadFile。而本服务的产物交付完全依赖
#        Delivery:PayloadRoot            （产物所在目录）
#        SkillCatalog:Skills.<code>.PayloadFile（产物文件名）
#    把 main 部署上来，appsettings.Production.json 里的 PayloadFile 会退化成一句
#    无效配置：**健康检查照样 OK、systemd 照样 active**，但买家付款后拿到的是
#    占位内容而不是源码包 —— 静默失败，比启动失败难发现得多。
#    脚本已在发布前加了交付能力自检（见 1/4 步），目标分支缺能力会直接中止。
#
#    临时部署其他分支：SKILLPAY_BRANCH=<分支名> bash deploy/deploy.sh

set -euo pipefail

SRC_DIR=/opt/skillpay/src
APP_DIR=/opt/skillpay/app
LOG_DIR=/var/log/skillpay
CONFIG_FILE="$APP_DIR/appsettings.Production.json"
BACKUP_FILE=/tmp/skillpay-prod-config.json
PORT=18080
BRANCH="${SKILLPAY_BRANCH:-release/source-v1.0.0}"

echo "==> 1/4 拉取最新代码（分支 $BRANCH）"
git -C "$SRC_DIR" fetch --all --prune

# 再按显式 refspec 拉一次目标分支，**不要**只依赖上面那条 --all。
# 原因（2026-09-18 实际踩到）：仓库的 remote.origin.fetch 若被收窄成只映射 main
#     fetch = +refs/heads/main:refs/remotes/origin/main
# 那么 "origin/release/source-v1.0.0" 这个 ref 从不被创建，下面的 reset 会把
# 参数当成路径而失败；旧脚本写死 origin/main 时更糟 —— 每次都把「交付版」
# 静默覆盖成「生成版」，健康检查照样通过、systemd 照样 active，
# 但买家付款后拿到的是占位内容。显式 refspec 能绕开这个配置陷阱。
git -C "$SRC_DIR" fetch --prune origin "+refs/heads/$BRANCH:refs/remotes/origin/$BRANCH"
git -C "$SRC_DIR" reset --hard "origin/$BRANCH"

# 交付能力自检：目标提交必须自带 DeliveryOptions，否则立刻中止。
# 放在最前面是有意的 —— 此时还没碰 APP_DIR，中止不产生任何副作用。
if ! git -C "$SRC_DIR" cat-file -e "HEAD:Configuration/DeliveryOptions.cs" 2>/dev/null; then
  echo "FATAL  分支 $BRANCH 不含产物交付能力（缺 Configuration/DeliveryOptions.cs）。"
  echo "       产物交付依赖 Delivery:PayloadRoot 与 SkillCatalog:Skills.<code>.PayloadFile；"
  echo "       部署「生成版」会让健康检查通过、但买家拿到占位内容。"
  echo "       已中止，$APP_DIR 未被改动。当前 src HEAD:"
  git -C "$SRC_DIR" log --oneline -1
  exit 1
fi

echo "==> 2/4 发布到 $APP_DIR"
had_config=0
if [ -f "$CONFIG_FILE" ]; then
  cp "$CONFIG_FILE" "$BACKUP_FILE"
  had_config=1
  echo "    已暂存现有生产配置"
fi

# 订单库（SQLite）落在 $APP_DIR/appdata 下，而下面要 rm -rf "$APP_DIR"。
# 不显式搬走，每次部署都会**清空全部订单记录** —— 已付款但尚未履约的订单会直接消失，
# 之后即使支付宝验付通过，本地也查不到订单，只能重新下发账单（钱等于白付）。
DATA_DIR="$APP_DIR/appdata"
DATA_BACKUP=/tmp/skillpay-appdata
had_data=0
if [ -d "$DATA_DIR" ]; then
  rm -rf "$DATA_BACKUP"
  cp -a "$DATA_DIR" "$DATA_BACKUP"
  had_data=1
  echo "    已暂存订单库 $(du -sh "$DATA_DIR" | cut -f1)"
fi

rm -rf "$APP_DIR"
mkdir -p "$APP_DIR" "$LOG_DIR"
dotnet publish "$SRC_DIR/SkillPay.Service.csproj" -c Release -o "$APP_DIR" --nologo

if [ "$had_config" = "1" ]; then
  cp "$BACKUP_FILE" "$CONFIG_FILE"
  echo "    已恢复生产配置"
else
  echo "    警告：未找到 $CONFIG_FILE —— 服务将因缺少必需配置而启动失败"
fi

if [ "$had_data" = "1" ]; then
  cp -a "$DATA_BACKUP" "$DATA_DIR"
  echo "    已恢复订单库"
fi

echo "==> 3/4 安装 systemd 单元"
install -m 644 "$SRC_DIR/deploy/skillpay.service" /etc/systemd/system/skillpay.service
systemctl daemon-reload
systemctl enable skillpay >/dev/null 2>&1 || true
systemctl restart skillpay

echo "==> 4/4 健康检查"
for _ in $(seq 1 20); do
  if curl -fsS "http://127.0.0.1:${PORT}/healthz" >/dev/null 2>&1; then
    echo "OK  healthz:"
    curl -s "http://127.0.0.1:${PORT}/healthz"
    echo
    exit 0
  fi
  sleep 1
done

echo "FAIL  健康检查未通过，最近日志："
journalctl -u skillpay -n 40 --no-pager || true
exit 1
