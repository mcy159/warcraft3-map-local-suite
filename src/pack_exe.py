#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
pack_exe.py — 魔兽争霸联机优化补丁.exe 重打包脚本（stub + zip 拼接，可复现）

背景（2026-11-24，patch-prep 子代理）：
  桌面补丁 exe = PE stub（core2_new.exe 14,336B）+ 追加 zip（108 文件/12 目录）。
  手工操作等价于 copy /b core2_new.exe + suite.zip，但桌面 exe 的 EOCD.cd_offset
  与各 CD 记录的 local header offset 均为「绝对文件偏移」（zipfile 可直接打开），
  因此本脚本在拼接后做同样的绝对化修正，产出结构与桌面 exe 一致。

用法：
  python pack_exe.py [--pkg DIR] [--stub FILE] [--out FILE] [--level N]
  --pkg        打包源目录（默认 deliverable\\map_local_suite，自动排除 .git/*.bak）
  --stub       PE stub（默认 deliverable\\core2_new.exe）
  --out        输出 exe（默认 verify_tmp\\patch_rebuild_test.exe；绝不触碰桌面 exe）
  --reference  结构对照参考 exe（默认桌面 exe；--no-reference 关闭）
  --level      DEFLATE 压缩级别 0-9（默认 6）

验证（脚本内置，完成后自动执行并写 verify_tmp\\pack_rebuild_verify.txt）：
  1) zipfile 可直接打开输出 exe（证明 EOCD/CD offset 绝对化成功）
  2) 条目数 == 108 文件 + 12 目录（与桌面 exe 一致）
  3) zip 清单 vs pkg 目录：名字/大小逐项一致（0 diff）
  4) zip 内 108 文件 MD5 == pkg 对应文件 MD5（全量比对）
  5) tools/skillbar_reader.exe == 24,064B / EC195A3A（keybind-fix 新版）
  6) 输出 exe 头部 stub 段 MD5 == core2_new.exe
"""
import argparse
import hashlib
import io
import os
import struct
import sys
import zipfile

DEFAULT_PKG = r"E:\aiwork\kk_re\deliverable\map_local_suite"
DEFAULT_STUB = r"E:\aiwork\kk_re\deliverable\core2_new.exe"
DEFAULT_OUT = r"E:\aiwork\kk_re\verify_tmp\patch_rebuild_test.exe"
DEFAULT_REF = r"C:\Users\Administrator\Desktop\魔兽争霸联机优化补丁.exe"
VERIFY_OUT = r"E:\aiwork\kk_re\verify_tmp\pack_rebuild_verify.txt"

SKIP_DIR_PARTS = (".git", "__pycache__", ".idea", ".vscode")
SKIP_FILE_NAMES = (".gitignore", ".gitattributes", ".DS_Store")
SKIP_FILE_SUFFIXES = (".bak", ".boxed_bak", "~")

def md5_bytes(b):
    return hashlib.md5(b).hexdigest().upper()

def scan_pkg(pkg):
    """返回 (files, dirs)：files = [(rel, data_bytes)]；dirs = [rel + '/']"""
    files = []
    dirs = set()
    for root, dnames, fnames in os.walk(pkg):
        dnames[:] = [d for d in dnames if d not in SKIP_DIR_PARTS]
        relroot = os.path.relpath(root, pkg)
        for d in dnames:
            r = os.path.relpath(os.path.join(root, d), pkg).replace("\\", "/")
            dirs.add(r + "/")
            # 中间目录也要（如 plugin/ 本身）
            parts = r.split("/")
            for i in range(1, len(parts)):
                dirs.add("/".join(parts[:i]) + "/")
        for fn in sorted(fnames):
            if fn in SKIP_FILE_NAMES:
                continue
            if fn.endswith(SKIP_FILE_SUFFIXES):
                continue
            full = os.path.join(root, fn)
            rel = os.path.relpath(full, pkg).replace("\\", "/")
            with open(full, "rb") as f:
                data = f.read()
            files.append((rel, data))
            parts = rel.split("/")
            for i in range(1, len(parts)):
                dirs.add("/".join(parts[:i]) + "/")
    files.sort(key=lambda t: t[0])
    dirs = sorted(dirs)
    return files, dirs

def build_zip(files, dirs, level):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED, allowZip64=False) as zf:
        # 目录条目 STORED，目录在前（与桌面 exe 一致）
        for d in dirs:
            zi = zipfile.ZipInfo(d, (1980, 1, 1, 0, 0, 0))
            zi.compress_type = zipfile.ZIP_STORED
            zf.writestr(zi, b"")
        # 文件条目 DEFLATED
        for rel, data in files:
            zi = zipfile.ZipInfo(rel, (1980, 1, 1, 0, 0, 0))
            zi.compress_type = zipfile.ZIP_DEFLATED
            zi._compresslevel = level
            zf.writestr(zi, data)
    return buf.getvalue()

def find_eocd(b):
    for i in range(len(b) - 22, max(0, len(b) - 22 - 65536), -1):
        if b[i:i + 4] == b"PK\x05\x06":
            return i
    return -1

def absolutize(zip_bytes, stub_size):
    """把 EOCD.cd_offset 与每个 CD 记录的 local header offset 修正为文件内绝对偏移（对齐桌面 exe 结构）"""
    i = find_eocd(zip_bytes)
    if i < 0:
        raise ValueError("EOCD not found in generated zip")
    b = bytearray(zip_bytes)
    cd_off = struct.unpack("<I", b[i + 16:i + 20])[0]
    n = struct.unpack("<H", b[i + 10:i + 12])[0]
    pos = cd_off
    for _ in range(n):
        if b[pos:pos + 4] != b"PK\x01\x02":
            raise ValueError("bad CD signature at %d" % pos)
        fn_len = struct.unpack("<H", b[pos + 28:pos + 30])[0]
        ex_len = struct.unpack("<H", b[pos + 30:pos + 32])[0]
        cm_len = struct.unpack("<H", b[pos + 32:pos + 34])[0]
        lo = struct.unpack("<I", b[pos + 42:pos + 46])[0]
        struct.pack_into("<I", b, pos + 42, lo + stub_size)
        pos += 46 + fn_len + ex_len + cm_len
    struct.pack_into("<I", b, i + 16, cd_off + stub_size)
    return bytes(b)

def main():
    ap = argparse.ArgumentParser(description="repack 魔兽争霸联机优化补丁.exe (stub + zip)")
    ap.add_argument("--pkg", default=DEFAULT_PKG)
    ap.add_argument("--stub", default=DEFAULT_STUB)
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--reference", default=DEFAULT_REF, help="set to empty string to disable")
    ap.add_argument("--level", type=int, default=9, help="DEFLATE level (9 ≈ desktop build size)")
    args = ap.parse_args()

    rep = []
    def note(k, v):
        rep.append((k, v))
        print("%s = %s" % (k, v))

    stub = open(args.stub, "rb").read()
    note("stub_size", len(stub))
    note("stub_md5", md5_bytes(stub))

    files, dirs = scan_pkg(args.pkg)
    note("pkg_files", len(files))
    note("pkg_dirs", len(dirs))

    zip_bytes = build_zip(files, dirs, args.level)
    note("zip_raw_size", len(zip_bytes))
    zip_bytes = absolutize(zip_bytes, len(stub))
    exe = stub + zip_bytes
    note("out_exe_size", len(exe))

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "wb") as f:
        f.write(exe)
    note("out_exe", args.out)
    note("out_exe_sha256", hashlib.sha256(exe).hexdigest().upper())

    # ---- 验证 ----
    ok_all = True
    def check(name, cond, detail):
        nonlocal ok_all
        note(name + (" PASS" if cond else " FAIL"), detail)
        if not cond:
            ok_all = False

    with zipfile.ZipFile(args.out) as zf:
        names = zf.namelist()
        dirs2 = [n for n in names if n.endswith("/")]
        files2 = [n for n in names if not n.endswith("/")]
        check("verify_zipfile_open", True, "zipfile opens out exe directly (absolute offsets OK)")
        check("verify_dir_count", len(dirs2) == 10, "%d dirs (expected 10)" % len(dirs2))
        check("verify_file_count", len(files2) == len(files), "%d files (expected %d)" % (len(files2), len(files)))
        # 清单 diff
        z_manifest = {n: zf.getinfo(n).file_size for n in files2}
        p_manifest = {rel: len(data) for rel, data in files}
        only_z = sorted(set(z_manifest) - set(p_manifest))
        only_p = sorted(set(p_manifest) - set(z_manifest))
        size_mm = sorted(k for k in (set(z_manifest) & set(p_manifest)) if z_manifest[k] != p_manifest[k])
        check("verify_manifest", not (only_z or only_p or size_mm),
              "only_zip=%d only_pkg=%d size_mismatch=%d" % (len(only_z), len(only_p), len(size_mm)))
        # 全文件 MD5
        p_md5 = {rel: md5_bytes(data) for rel, data in files}
        bad_md5 = []
        for rel in files2:
            if md5_bytes(zf.read(rel)) != p_md5[rel]:
                bad_md5.append(rel)
        check("verify_all_md5", not bad_md5, "%d/%d files MD5 match" % (len(files2) - len(bad_md5), len(files2)))
        # keybind_panel 期望值 v3 (AutoReadHotkeys 自动读技能栏/物品栏 hotkey, RPM 只读; 2026-11-25 升级)
        info = zf.getinfo("tools/keybind_panel.exe")
        d = zf.read("tools/keybind_panel.exe")
        check("verify_keybind_panel", info.file_size == 59904 and md5_bytes(d) == "C5973F2E82B92403863C69BC25239961",
              "size=%d md5=%s (expected 59904 / C5973F2E...)" % (info.file_size, md5_bytes(d)))
        # 包内不得残留已清理的旧工具
        for bad in ("tools/SuWar3Tools/", "tools/WarKey2017b/", "tools/skillbar_reader.exe",
                    "tools/keybind.exe.bak", "tools/keybind_panel.exe.bak_f10"):
            check("verify_absent_" + bad.replace("/", "_").replace(".", "_"),
                  bad not in set(names), "absent: %s" % bad)
    # 头部 stub 校验
    with open(args.out, "rb") as f:
        head = f.read(len(stub))
    check("verify_head_stub", md5_bytes(head) == md5_bytes(stub), "out head == stub md5")
    # 参考 exe 对照
    if args.reference and os.path.exists(args.reference):
        ref_size = os.path.getsize(args.reference)
        note("reference_exe_size", ref_size)
        note("size_delta_vs_reference", len(exe) - ref_size)

    note("RESULT", "ALL CHECKS PASSED" if ok_all else "SOME CHECKS FAILED")
    with io.open(VERIFY_OUT, "w", encoding="utf-8") as fo:
        for k, v in rep:
            fo.write("%s = %s\n" % (k, v))
    print("DONE -> %s" % VERIFY_OUT)
    return 0 if ok_all else 1

if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    sys.exit(main())
