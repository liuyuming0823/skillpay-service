#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SkillPay 交付包打包器（发布方工具，纯标准库，无第三方依赖）

解决的问题：以前打交付包是手工挑文件、手工压缩、手工传服务器、手工改配置，
必然漂移 —— 生产现役包曾比仓库留档包多出 3 个文档修订，且没人发现。

用法（在仓库根目录执行）：

    # 1. 打包（输出到 dist/）
    python scripts/pack-dist.py pack --version v1.1.0

    # 2. 只看会打进哪些文件，不落盘
    python scripts/pack-dist.py pack --version v1.1.0 --dry-run

    # 3. 校验一个已有的包（重点：查有没有把生产密钥打进去）
    python scripts/pack-dist.py verify dist/paid-skill-service-source-v1.1.0.zip

    # 4. 一条龙：打包 + 上传 + 切配置 + 重启 + 自检（会改生产，需显式加 --yes-i-know）
    python scripts/pack-dist.py pack --version v1.1.0 --deploy --yes-i-know

设计要点：
  * 打包根是「当前工作区」，不是 git HEAD —— 这样未提交的改动也能进包，
    与「改完立刻打包验证」的实际节奏一致；脚本会打印未提交改动作为提醒。
  * 排除规则是**黑名单**而不是白名单：新增源码文件（如新加的 Options 类）
    不需要改脚本就能进包，符合直觉。
  * 包内顶层目录固定为 skillpay-service/，文件权限统一 0644，
    时间戳统一取 git HEAD 的提交时间 → 同一份源码重复打包字节级一致。
  * 打包名与生产 PayloadFile 同名（paid-skill-service-source-<version>.zip），
    少一处「打包名 ↔ 配置值」的手工对应。
"""

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import zipfile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(SCRIPT_DIR)
PACKAGE_DIR_NAME = "skillpay-service"
DEFAULT_OUT_DIR = os.path.join(REPO_ROOT, "dist")

# 生产环境参数（--deploy 用）
PROD_HOST = os.environ.get("SKILLPAY_HOST", "ubuntu@111.230.145.82")
PROD_PAYLOAD_DIR = "/opt/skillpay/payloads"
PROD_APP_DIR = "/opt/skillpay/app"
PROD_CONFIG = PROD_APP_DIR + "/appsettings.Production.json"
PROD_SERVICE = "skillpay"
PROD_PORT = 18080

# ── 排除规则 ────────────────────────────────────────────────────────────────
# 目录名：命中即整棵子树跳过
EXCLUDE_DIRS = {
    ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules",
    "appdata",        # 运行期 SQLite 订单库，绝不外发
    "dist",           # 打包产物自身，避免自我嵌套
    "skills",         # 发布方本地的技能包留档
    "Resources",      # 生成版（main）的提示词规格，属「值钱的部分」
    "%SystemDrive%",  # 历史测试残留的缓存位移目录
    ".pytest_cache", "__pycache__",
}

# 文件名（相对仓库根，统一用 / 分隔）：命中即排除
EXCLUDE_FILE_PATTERNS = [
    r"^appsettings\.Production\.json$",          # 真实生产配置：含私钥，绝不能进包
    r"^appsettings\.(TestEnforce|Verify|Local|Test)\..*\.json$",
    r"^appsettings\..*\.local\.json$",
    r".*\.bak$",
    r".*\.bak-\d+$",
    r".*\.orig$",
    r".*\.zip$",                                  # 任何压缩包（含 dist/ 的留档包）
    r".*\.log$",
    r".*\.pem$",
    r".*\.key$",
    r".*\.p12$",
    r"^ico\.png$",
    r"^\.DS_Store$",
    r".*/\.DS_Store$",
    r"^Thumbs\.db$",
    r"^scripts/pack-dist\.py$",                   # 发布方工具，默认不外发
    r"^scripts/release-dist\.sh$",
]

# 这些是「买家装到源码包里绝对不该出现」的，verify 会重点报
FORBIDDEN_ALWAYS = [
    (re.compile(r".*/appsettings\.Production\.json$"), "真实生产配置（含支付宝私钥）"),
    (re.compile(r".*/appdata/.*"), "运行期订单库"),
    (re.compile(r".*\.pem$"), "证书/私钥文件"),
    (re.compile(r".*\.key$"), "证书/私钥文件"),
    (re.compile(r".*/\.git/.*"), "git 内部数据"),
    (re.compile(r".*/dist/.*"), "打包产物自身"),
]


def _rel(path):
    return os.path.relpath(path, REPO_ROOT).replace(os.sep, "/")


def _is_excluded(rel):
    parts = rel.split("/")
    for p in parts[:-1]:
        if p in EXCLUDE_DIRS:
            return "目录 %s/" % p
    if parts[-1] in EXCLUDE_DIRS:
        return "目录 %s/" % parts[-1]
    for pat in EXCLUDE_FILE_PATTERNS:
        if re.match(pat, rel):
            return "匹配规则 %s" % pat
    return None


def collect_files(verbose=False):
    """遍历工作区，返回 [(绝对路径, 包内相对路径)]"""
    picked, skipped = [], []
    for dirpath, dirnames, filenames in os.walk(REPO_ROOT):
        # 就地裁剪，避免走进 .git / bin / obj 等
        keep = []
        for d in sorted(dirnames):
            if d in EXCLUDE_DIRS:
                skipped.append((os.path.join(dirpath, d), "目录 %s/" % d))
            else:
                keep.append(d)
        dirnames[:] = keep

        for fn in sorted(filenames):
            full = os.path.join(dirpath, fn)
            rel = _rel(full)
            why = _is_excluded(rel)
            if why:
                skipped.append((full, why))
                continue
            picked.append((full, rel))
    return picked, skipped


def git_head_info():
    """取 HEAD 短 sha 与提交时间（作为包内统一时间戳，保证可复现）"""
    try:
        sha = subprocess.check_output(
            ["git", "-C", REPO_ROOT, "rev-parse", "--short", "HEAD"],
            stderr=subprocess.DEVNULL).decode().strip()
        ts = subprocess.check_output(
            ["git", "-C", REPO_ROOT, "log", "-1", "--format=%ct"],
            stderr=subprocess.DEVNULL).decode().strip()
        return sha, int(ts)
    except Exception:
        return "unknown", 1704067200  # 2024-01-01 兜底，保证可复现


def git_dirty_summary():
    try:
        out = subprocess.check_output(
            ["git", "-C", REPO_ROOT, "status", "--porcelain"],
            stderr=subprocess.DEVNULL).decode("utf-8", "replace").strip()
        return out
    except Exception:
        return ""


def build_zip(files, out_path, stamp):
    os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
    if os.path.exists(out_path):
        os.remove(out_path)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for full, rel in files:
            arc = "%s/%s" % (PACKAGE_DIR_NAME, rel)
            zi = zipfile.ZipInfo(arc, date_time=stamp)
            zi.external_attr = 0o644 << 16
            zi.compress_type = zipfile.ZIP_DEFLATED
            with open(full, "rb") as fh:
                z.writestr(zi, fh.read())
    return out_path


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def zip_member_shas(path):
    """包内 {成员名: (大小, sha256)}，用于校验与比对"""
    out = {}
    with zipfile.ZipFile(path) as z:
        for i in z.infolist():
            if i.is_dir():
                continue
            data = z.read(i.filename)
            out[i.filename] = (len(data), hashlib.sha256(data).hexdigest())
    return out


# ── 子命令 ─────────────────────────────────────────────────────────────────

def cmd_pack(args):
    version = args.version
    if not re.match(r"^v?\d+\.\d+\.\d+$", version):
        print("ERROR 版本号格式应为 vX.Y.Z 或 X.Y.Z，收到：%s" % version)
        print("      版本号是订单冻结标识（写入 PayloadVersion），必须显式、稳定、不可复用。")
        return 2
    if not version.startswith("v"):
        version = "v" + version

    files, skipped = collect_files()
    if not files:
        print("ERROR 没有收集到任何文件，检查是否在仓库根目录执行")
        return 1

    name = args.name or "paid-skill-service-source-%s.zip" % version
    out_path = os.path.join(args.out, name)

    sha_head, commit_ts = git_head_info()

    print("仓库根     : %s" % REPO_ROOT)
    print("HEAD       : %s" % sha_head)
    print("版本       : %s" % version)
    print("包内顶层   : %s/" % PACKAGE_DIR_NAME)
    print("待打包     : %d 个文件" % len(files))
    print("已排除     : %d 项" % len(skipped))
    if args.show_excluded:
        for full, why in skipped:
            print("    - %-52s (%s)" % (_rel(full), why))

    dirty = git_dirty_summary()
    if dirty:
        print("")
        print("提示：工作区有未提交改动，这些改动会一并进包（%d 项）：" % len(dirty.splitlines()))
        for line in dirty.splitlines()[:12]:
            print("    %s" % line)
        if len(dirty.splitlines()) > 12:
            print("    ... 其余 %d 项" % (len(dirty.splitlines()) - 12))
        print("      发布前请确认这些改动已经过验证，并在打包后提交，否则包与 commit 对不上。")

    if args.dry_run:
        print("")
        print("[dry-run] 未写出文件。包内将包含：")
        for _, rel in files:
            print("    %s/%s" % (PACKAGE_DIR_NAME, rel))
        return 0

    stamp = None
    if not args.no_stamp:
        import time as _t
        stamp = _t.gmtime(commit_ts)[0:6]
    else:
        stamp = (1980, 1, 1, 0, 0, 0)

    build_zip(files, out_path, stamp)
    size = os.path.getsize(out_path)
    digest = sha256_file(out_path)

    print("")
    print("已生成     : %s" % out_path)
    print("大小       : %d 字节" % size)
    print("sha256     : %s" % digest)

    # 自校验：禁入项必须为 0
    bad = check_forbidden(out_path)
    if bad:
        print("")
        print("FATAL 包内出现禁入文件，请立即删除该包：")
        for m, why in bad:
            print("    - %s (%s)" % (m, why))
        return 3

    if args.deploy:
        return do_deploy(out_path, name, version, digest, args)
    return 0


def check_forbidden(zip_path):
    hits = []
    with zipfile.ZipFile(zip_path) as z:
        for n in z.namelist():
            for rx, why in FORBIDDEN_ALWAYS:
                if rx.match(n):
                    hits.append((n, why))
                    break
    return hits


def cmd_verify(args):
    path = args.zip
    if not os.path.exists(path):
        print("ERROR 文件不存在：%s" % path)
        return 1
    members = zip_member_shas(path)
    size = os.path.getsize(path)
    print("包         : %s" % path)
    print("大小       : %d 字节" % size)
    print("sha256     : %s" % sha256_file(path))
    print("文件数     : %d" % len(members))

    tops = sorted({n.split("/")[0] for n in members})
    print("顶层目录   : %s" % ", ".join(tops))

    bad = check_forbidden(path)
    print("")
    if bad:
        print("FATAL 发现 %d 个禁入文件：" % len(bad))
        for m, why in bad:
            print("    - %s (%s)" % (m, why))
        return 3
    print("禁入检查   : 通过（无生产密钥 / 无订单库 / 无 git 数据 / 无嵌套包）")

    print("")
    print("包内清单：")
    for n in sorted(members):
        print("    %-52s %7dB" % (n, members[n][0]))

    if args.against:
        other = zip_member_shas(args.against)
        print("")
        print("与 %s 对比：" % args.against)
        only_a = sorted(set(members) - set(other))
        only_b = sorted(set(other) - set(members))
        diff = sorted(n for n in set(members) & set(other) if members[n][1] != other[n][1])
        print("    仅本包有   : %d %s" % (len(only_a), only_a or ""))
        print("    仅对照包有 : %d %s" % (len(only_b), only_b or ""))
        print("    内容不同   : %d %s" % (len(diff), diff or ""))
        return 0 if not (only_a or only_b or diff) else 4
    return 0


# ── 生产部署（走 ssh，默认不执行） ──────────────────────────────────────────

def _ssh_script(script):
    """把脚本从 stdin 交给远端 bash 执行 —— 避开引号/换行/百分号的转义地狱

    注意：Windows 上 text=True 会把喂给 stdin 的 \\n 翻译成 \\r\\n，远端 bash
    读到的是 "set -euo pipefail\\r"，\\r 会让选项名变成无效值，脚本在第 2 行
    就死掉（报 `set: pipefail: invalid option name`）。所以这里显式以二进制
    喂 stdin、再手工按 utf-8 解码输出，保持与原来一致的 str 接口。
    """
    r = subprocess.run(
        ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20", PROD_HOST, "bash -s"],
        input=script.replace("\r\n", "\n").replace("\r", "\n").encode("utf-8"),
        capture_output=True)
    return subprocess.CompletedProcess(
        r.args, r.returncode,
        (r.stdout or b"").decode("utf-8", "replace"),
        (r.stderr or b"").decode("utf-8", "replace"))


def _scp(local, remote):
    return subprocess.run(
        ["scp", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20", local, remote],
        capture_output=True, text=True)


# 远端部署脚本模板。占位符用 @@X@@ 而不是 {} 或 %，
# 因为脚本内部同时含 bash 的 ${VAR} 与 Python 的 {} 字面量。
# sudo 一律带 -n：无 tty 时应当立刻报「需要密码」而不是挂住。
DEPLOY_SCRIPT = r"""#!/usr/bin/env bash
set -euo pipefail

NEW="@@NAME@@"
VER="@@VER@@"
SHA="@@SHA@@"
TMP="/tmp/@@NAME@@"
DIR="@@DIR@@"
CFG="@@CFG@@"
SVC="@@SVC@@"
PORT=@@PORT@@

echo "== 1/5 安装交付包 =="
sudo -n install -m 644 "$TMP" "$DIR/$NEW"
echo "   已写入 $DIR/$NEW"

echo "== 2/5 校验落盘 sha256 =="
got=$(sha256sum "$DIR/$NEW" | cut -d' ' -f1)
if [ "$got" != "$SHA" ]; then
  echo "   FATAL 落盘 sha 不一致"
  echo "     期望 $SHA"
  echo "     实际 $got"
  exit 1
fi
echo "   OK 与本地打包结果一致"

echo "== 3/5 切换配置（老包保留，只改三个键） =="
sudo -n cp "$CFG" "$CFG.bak-$(date +%s)"
sudo -n python3 - "$CFG" "$NEW" "$VER" <<'PYEOF'
import json, sys
cfg, fname, ver = sys.argv[1], sys.argv[2], sys.argv[3]
with open(cfg, encoding="utf-8") as fh:
    d = json.load(fh)
skills = (d.get("SkillCatalog") or {}).get("Skills") or {}
if not skills:
    print("   WARN SkillCatalog.Skills 为空，配置未改动")
for code, item in skills.items():
    if item.get("PayloadFile"):
        item["PayloadFile"] = fname
        item["PayloadFileName"] = fname
        item["PayloadVersion"] = ver
        print("   " + code + " -> " + fname + " (" + ver + ")")
with open(cfg, "w", encoding="utf-8") as fh:
    json.dump(d, fh, ensure_ascii=False, indent=2)
    fh.write("\n")
PYEOF

echo "== 4/5 重启服务 =="
sudo -n systemctl restart "$SVC"
sleep 3

echo "== 5/5 自检 =="
if curl -fsS "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1; then
  echo "   OK healthz"
else
  echo "   FATAL healthz 未通过，最近日志："
  sudo -n tail -n 30 /var/log/skillpay/app.log 2>/dev/null || true
  exit 1
fi
echo "   资源目录："
curl -s "http://127.0.0.1:$PORT/" | head -c 500
echo
echo "完成。旧包仍留在 $DIR，历史订单继续拿到原版本。"
"""


def render_deploy_script(name, version, digest):
    return (DEPLOY_SCRIPT
            .replace("@@NAME@@", name)
            .replace("@@VER@@", version)
            .replace("@@SHA@@", digest)
            .replace("@@DIR@@", PROD_PAYLOAD_DIR)
            .replace("@@CFG@@", PROD_CONFIG)
            .replace("@@SVC@@", PROD_SERVICE)
            .replace("@@PORT@@", str(PROD_PORT)))


def do_deploy(zip_path, name, version, digest, args):
    if not args.yes_i_know:
        print("")
        print("已跳过部署：--deploy 需要同时加 --yes-i-know（会改生产配置并重启服务）。")
        print("本次只完成打包，包在 %s" % zip_path)
        return 0

    print("")
    print("== 部署到生产 %s ==" % PROD_HOST)
    remote_tmp = "/tmp/%s" % name

    r = _scp(zip_path, "%s:%s" % (PROD_HOST, remote_tmp))
    if r.returncode != 0:
        print("FAIL scp 上传失败：%s" % (r.stderr or r.stdout))
        return 1
    print("OK   已上传到 %s" % remote_tmp)

    r = _ssh_script(render_deploy_script(name, version, digest))
    if r.stdout:
        print(r.stdout.rstrip())
    if r.returncode != 0:
        print("")
        print("FAIL 远端执行失败（退出码 %d）" % r.returncode)
        if r.stderr:
            print(r.stderr.rstrip())
        print("")
        print("排查提示：")
        print("  * 报 'a password is required' → 服务器 sudo 需要密码，自动化跑不通。")
        print("    在服务器执行 sudo visudo，给 ubuntu 加 NOPASSWD 规则。")
        print("  * 报 sha 不一致 → 传输损坏，重跑即可；老包未被替换，服务无影响。")
        return 1
    return 0


def cmd_doctor(args):
    """部署环境预检：一次跑完，避免 --deploy 跑到一半才发现权限不对"""
    print("== 本地 ==")
    sha_head, _ = git_head_info()
    print("  仓库根        : %s" % REPO_ROOT)
    print("  HEAD          : %s" % sha_head)
    dirty = git_dirty_summary()
    print("  未提交改动    : %d 项%s" % (len(dirty.splitlines()),
                                        "（打包会一并包含，记得随后提交）" if dirty else ""))
    files, skipped = collect_files()
    print("  可打包文件    : %d 个（排除 %d 项）" % (len(files), len(skipped)))
    bad_local = []
    for full, rel in files:
        for rx, why in FORBIDDEN_ALWAYS:
            if rx.match("%s/%s" % (PACKAGE_DIR_NAME, rel)):
                bad_local.append((rel, why))
                break
    print("  禁入文件自检  : %s" % ("通过" if not bad_local else "FATAL %s" % bad_local))

    print("")
    print("== 生产 %s ==" % PROD_HOST)
    probe = (
        'echo "SSH_OK=SSH_OK"\n'
        'echo "SUDO_NOPASSWD=$(sudo -n true 2>/dev/null && echo yes || echo no)"\n'
        'echo "PAYLOAD_DIR=$(test -d @@DIR@@ && echo yes || echo no)"\n'
        'echo "PAYLOAD_WRITABLE=$(sudo -n test -w @@DIR@@ 2>/dev/null && echo yes || echo no)"\n'
        'echo "CONFIG=$(test -f @@CFG@@ && echo yes || echo no)"\n'
        'echo "SERVICE=$(systemctl is-active @@SVC@@ 2>/dev/null)"\n'
        'echo "PYTHON=$(python3 -V 2>&1)"\n'
        'echo "EXISTING=$(ls -1 @@DIR@@ 2>/dev/null | tr \'\\n\' \' \')"\n'
    ).replace("@@DIR@@", PROD_PAYLOAD_DIR).replace("@@CFG@@", PROD_CONFIG).replace("@@SVC@@", PROD_SERVICE)

    r = _ssh_script(probe)
    if r.returncode != 0 and not r.stdout:
        print("  SSH 连接失败：%s" % (r.stderr or "").strip()[:200])
        print("  自动化部署不可用；打包功能不受影响。")
        return 1

    got = {}
    for line in r.stdout.splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            got[k.strip()] = v.strip()
    for k, label in [("SSH_OK", "SSH 免密登录"),
                     ("SUDO_NOPASSWD", "sudo 免密"),
                     ("PAYLOAD_DIR", "交付目录存在"),
                     ("PAYLOAD_WRITABLE", "交付目录可写"),
                     ("CONFIG", "生产配置存在"),
                     ("SERVICE", "服务运行中"),
                     ("PYTHON", "python3")]:
        v = got.get(k, "(无)")
        mark = "OK  " if (k in ("SSH_OK",) and v == "SSH_OK") or v in ("yes", "active") or k == "PYTHON" else "WARN"
        print("  %-4s %-12s : %s" % (mark, label, v))
    print("  现有交付包    : %s" % got.get("EXISTING", "(无)"))

    blocking = (got.get("SUDO_NOPASSWD") != "yes") or (got.get("PAYLOAD_WRITABLE") != "yes")
    print("")
    if blocking:
        print("结论：打包可用；自动部署**不可用**（sudo 或目录权限不足）。")
        print("      在服务器执行 sudo visudo，为 ubuntu 添加：ubuntu ALL=(ALL) NOPASSWD: ALL")
        return 1
    print("结论：打包与自动部署都就绪（python scripts/pack-dist.py pack --version vX.Y.Z --deploy --yes-i-know）")
    return 0


def main():
    ap = argparse.ArgumentParser(
        description="SkillPay 交付包打包器",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__)
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("pack", help="打包交付包")
    p.add_argument("--version", required=True, help="版本号，如 v1.1.0（写入 PayloadVersion）")
    p.add_argument("--name", default=None, help="包文件名，默认 paid-skill-service-source-<版本>.zip")
    p.add_argument("--out", default=DEFAULT_OUT_DIR, help="输出目录，默认 dist/")
    p.add_argument("--dry-run", action="store_true", help="只列清单，不写文件")
    p.add_argument("--show-excluded", action="store_true", help="打印被排除的每一项及原因")
    p.add_argument("--no-stamp", action="store_true", help="时间戳用固定值（调试用）")
    p.add_argument("--deploy", action="store_true", help="打包后上传并切换生产（危险）")
    p.add_argument("--yes-i-know", action="store_true", help="确认执行生产变更")
    p.set_defaults(func=cmd_pack)

    v = sub.add_parser("verify", help="校验已有包的禁入项与清单")
    v.add_argument("zip", help="待校验的 zip 路径")
    v.add_argument("--against", default=None, help="同时与另一个包逐文件比对")
    v.set_defaults(func=cmd_verify)

    d = sub.add_parser("doctor", help="部署环境预检（SSH / sudo / 目录权限 / 服务状态）")
    d.set_defaults(func=cmd_doctor)

    args = ap.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
