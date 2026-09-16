#!/usr/bin/env bash
#
# SkillPay 上线 / 更新脚本（在服务器上执行，幂等可重复运行）
#
#   bash /opt/skillpay/src/deploy/deploy.sh
#
# 做四件事：拉最新代码 → 发布到 /opt/skillpay/app → 安装 systemd 单元 → 健康检查
# 注意：会保留 appsettings.Production.json（生产密钥不在仓库里，必须原地保留）

set -euo pipefail

SRC_DIR=/opt/skillpay/src
APP_DIR=/opt/skillpay/app
LOG_DIR=/var/log/skillpay
CONFIG_FILE="$APP_DIR/appsettings.Production.json"
BACKUP_FILE=/tmp/skillpay-prod-config.json
PORT=18080

echo "==> 1/4 拉取最新代码"
git -C "$SRC_DIR" fetch --all --prune
git -C "$SRC_DIR" reset --hard origin/main

echo "==> 2/4 发布到 $APP_DIR"
had_config=0
if [ -f "$CONFIG_FILE" ]; then
  cp "$CONFIG_FILE" "$BACKUP_FILE"
  had_config=1
  echo "    已暂存现有生产配置"
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
