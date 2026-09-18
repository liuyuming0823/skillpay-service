#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把本地技能目录打包，用于**上架 SkillHub**。

⚠️ 先把两件事分清楚，这里最容易混：

    ┌ 交付物（卖给客户的）  = 支付服务的**源码 + 教程**
    │   → scripts/pack-dist.py 产出，放在服务端 payloads/，经 /result 按订单发
    │   → 例：paid-skill-service-source-v1.1.0.zip
    │
    └ 技能包（本脚本产出的） = **取货器**，装在本地的 SKILL.md 与脚本
        → 上架 SkillHub 用；本身免费、不收费
        → 例：ym-pay-service-source-1.4.0.zip

    客户拿到的是「源码包」，不是技能包。技能包只是去买源码包的工具。

为什么需要这个脚本
------------------
技能目录改动后，仓库 ``skills/`` 下那份 zip 常常忘了重打，于是出现隐蔽漂移：

    改完 ``scripts/sync.py``（版本自检数据源从「本服务端」换成 SkillHub）之后，
    仓库里的 zip 没重新打 —— 发出去的仍是**依赖旧后端的取货器**。

这种漂移不会报错，只会让人装到一个已经过时的取货器，很难发现。
``--check`` 就是用来在提交/上架前抓出这种漂移的。

用法
----
    python scripts/pack-skill.py                     # 打 pack，输出到 skills/
    python scripts/pack-skill.py --skill-dir <路径>   # 指定技能目录
    python scripts/pack-skill.py --check             # 只校验已存在的包是否为最新（不改文件）

退出码
------
    0  正常（pack 成功 / check 一致）
    1  失败（找不到源目录、SKILL.md 缺 version 等）
    2  check 模式下包已过期，需要重新打包
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import sys
import zipfile

# 默认技能目录：WorkBuddy 的用户级技能目录
DEFAULT_SKILL_DIR = os.path.join(
    os.path.expanduser("~"), ".workbuddy", "skills", "ym-pay-service-source"
)

# 不进包的文件：_meta.json 是本地安装元数据，属于「本机状态」而非技能内容；
# 发出去只会让买家的目录多出一份来路不明的状态文件。
EXCLUDE_NAMES = {"_meta.json", ".DS_Store", "Thumbs.db"}
EXCLUDE_DIRS = {"__pycache__", ".git", ".idea", ".vscode"}
EXCLUDE_SUFFIX = (".pyc", ".pyo", ".tmp", ".swp")

# 固定时间戳：让同一份源码重复打包字节级一致，sha 才有比对意义。
FIXED_ZIP_DATE = (1980, 1, 1, 0, 0, 0)


def repo_root() -> str:
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def read_version(skill_dir: str) -> str:
    """从 SKILL.md 的 YAML frontmatter 里取 version。"""
    skill_md = os.path.join(skill_dir, "SKILL.md")
    if not os.path.isfile(skill_md):
        raise SystemExit("FAIL  找不到 %s" % skill_md)
    with open(skill_md, encoding="utf-8") as fh:
        head = fh.read(4096)
    m = re.search(r"^version:\s*([0-9][0-9A-Za-z.\-]*)", head, re.MULTILINE)
    if not m:
        raise SystemExit("FAIL  %s 的 frontmatter 里没有 version" % skill_md)
    return m.group(1).strip()


def read_skill_code(skill_dir: str) -> str:
    skill_md = os.path.join(skill_dir, "SKILL.md")
    with open(skill_md, encoding="utf-8") as fh:
        head = fh.read(4096)
    m = re.search(r"^name:\s*(\S+)", head, re.MULTILINE)
    return m.group(1).strip() if m else os.path.basename(skill_dir.rstrip("/\\"))


def collect_files(skill_dir: str) -> list[str]:
    """收集要入包的文件，返回相对路径（统一用 / 分隔），已排序。"""
    out: list[str] = []
    for root, dirs, files in os.walk(skill_dir):
        dirs[:] = [d for d in dirs if d not in EXCLUDE_DIRS]
        for name in files:
            if name in EXCLUDE_NAMES or name.endswith(EXCLUDE_SUFFIX):
                continue
            full = os.path.join(root, name)
            rel = os.path.relpath(full, skill_dir).replace(os.sep, "/")
            out.append(rel)
    return sorted(out)


def build_zip(skill_dir: str, files: list[str], zip_path: str) -> None:
    os.makedirs(os.path.dirname(zip_path), exist_ok=True)
    tmp = zip_path + ".tmp"
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for rel in files:
            info = zipfile.ZipInfo(rel, date_time=FIXED_ZIP_DATE)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16  # 统一权限，避免把本机权限带出去
            with open(os.path.join(skill_dir, rel.replace("/", os.sep)), "rb") as fh:
                z.writestr(info, fh.read())
    os.replace(tmp, zip_path)


def sha256_of(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def zip_content_sha(zip_path: str) -> tuple[str, list[str]]:
    """对包内「路径 + 内容」求一个整体指纹，用来判断包是否过期。"""
    h = hashlib.sha256()
    with zipfile.ZipFile(zip_path) as z:
        names = sorted(z.namelist())
        for n in names:
            h.update(n.encode("utf-8"))
            h.update(b"\0")
            h.update(hashlib.sha256(z.read(n)).digest())
    return h.hexdigest(), names


def dir_content_sha(skill_dir: str, files: list[str]) -> str:
    h = hashlib.sha256()
    for rel in files:
        h.update(rel.encode("utf-8"))
        h.update(b"\0")
        with open(os.path.join(skill_dir, rel.replace("/", os.sep)), "rb") as fh:
            h.update(hashlib.sha256(fh.read()).digest())
    return h.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser(description="打包取货器技能包到 skills/")
    ap.add_argument("--skill-dir", default=DEFAULT_SKILL_DIR, help="技能源目录")
    ap.add_argument("--out-dir", default=os.path.join(repo_root(), "skills"), help="输出目录")
    ap.add_argument("--check", action="store_true", help="只校验已存在的包是否最新，不写文件")
    args = ap.parse_args()

    skill_dir = os.path.abspath(args.skill_dir)
    if not os.path.isdir(skill_dir):
        print("FAIL  技能目录不存在：%s" % skill_dir)
        return 1

    code = read_skill_code(skill_dir)
    version = read_version(skill_dir)
    name = "%s-%s.zip" % (code, version)
    zip_path = os.path.join(os.path.abspath(args.out_dir), name)

    files = collect_files(skill_dir)
    want = dir_content_sha(skill_dir, files)

    print("技能编码 : %s" % code)
    print("版本     : %s" % version)
    print("源目录   : %s" % skill_dir)
    print("目标包   : %s" % zip_path)
    print("入包文件 : %d 个" % len(files))
    for rel in files:
        print("    %s" % rel)

    if args.check:
        if not os.path.isfile(zip_path):
            print("")
            print("结果     : 包不存在，需要打包")
            return 2
        got, names = zip_content_sha(zip_path)
        same = got == want and names == files
        print("")
        if same:
            print("结果     : ✓ 包与源目录一致（%s）" % sha256_of(zip_path)[:16])
            return 0
        print("结果     : ✗ 包已过期，需要重新打包")
        miss = [n for n in files if n not in names]
        extra = [n for n in names if n not in files]
        if miss:
            print("           源目录有、包内缺：%s" % ", ".join(miss))
        if extra:
            print("           包内有、源目录无：%s" % ", ".join(extra))
        if not miss and not extra:
            print("           同名文件内容有差异")
        return 2

    build_zip(skill_dir, files, zip_path)
    digest = sha256_of(zip_path)
    size = os.path.getsize(zip_path)
    print("")
    print("OK      已生成 %s" % zip_path)
    print("        大小 %d bytes / sha256 %s" % (size, digest))
    print("")
    print("提醒：appsettings.json 的 SkillUpdate.Skills.<code>.Version（=%s）与" % version)
    print("      PackageFile（=%s）要与本次包名一致，否则服务端 manifest 会指向旧包。" % name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
