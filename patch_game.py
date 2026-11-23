# -*- coding: utf-8 -*-
"""从 1.27 原版 Game.dll 重建 Game_patched2.dll (地图大小限制 8MB -> 1GB)。

用法:
    python patch_game.py [原版Game.dll路径] [输出路径]
    (缺省: 输入 Game_orig.dll, 输出 Game_patched2.dll)

原理: 对 Game.dll 做 7 处字节补丁 (全部同长度替换, 无需移动代码):
  1. RVA 0x3535E5  cmp ecx,8; jae +4   -> NOP x5    (解除地图大小判定入口)
  2. RVA 0x30F930  mov eax,8           -> mov eax,0x40000000
  3. RVA 0x4EB290  mov eax,0x800000    -> mov eax,0x40000000
  4. RVA 0x60E810  mov eax,0x800000    -> mov eax,0x40000000
  5. RVA 0x84F530  cmp eax,0x800000    -> cmp eax,0x40000000
  6. RVA 0x85F9B6  cmp eax,0x800000    -> cmp eax,0x40000000
  7. RVA 0x872666  cmp eax,0x800000    -> cmp eax,0x40000000

补丁后文件 MD5 必须为 690A546C9567C598262518E830286CC3, 否则报错退出。
"""
import struct, sys, hashlib

ORIG_MD5 = "267861A0DFD416DBAD13E7EE3EC7794A"
PATCHED_MD5 = "690A546C9567C598262518E830286CC3"

PATCHES = [
    (0x3535E5, bytes.fromhex("83 F9 08 73 04"), bytes.fromhex("90 90 90 90 90")),
    (0x30F930, bytes.fromhex("B8 08 00 00 00"), bytes.fromhex("B8 00 00 00 40")),
    (0x4EB290, bytes.fromhex("B8 00 00 80 00"), bytes.fromhex("B8 00 00 00 40")),
    (0x60E810, bytes.fromhex("B8 00 00 80 00"), bytes.fromhex("B8 00 00 00 40")),
    (0x84F530, bytes.fromhex("3D 00 00 80 00"), bytes.fromhex("3D 00 00 00 40")),
    (0x85F9B6, bytes.fromhex("3D 00 00 80 00"), bytes.fromhex("3D 00 00 00 40")),
    (0x872666, bytes.fromhex("3D 00 00 80 00"), bytes.fromhex("3D 00 00 00 40")),
]


def rva_to_offset(data, rva):
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    optsz = struct.unpack_from("<H", data, e_lfanew + 20)[0]
    sec = e_lfanew + 24 + optsz
    for i in range(nsec):
        va, vs, prd, srd = struct.unpack_from("<IIII", data, sec + i * 40 + 12)
        if va <= rva < va + max(vs, srd):
            return prd + (rva - va)
    raise ValueError("RVA %08X not mapped" % rva)


def md5hex(b):
    return hashlib.md5(b).hexdigest().upper()


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else "Game_orig.dll"
    dst = sys.argv[2] if len(sys.argv) > 2 else "Game_patched2.dll"
    data = bytearray(open(src, "rb").read())
    h = md5hex(bytes(data))
    if h != ORIG_MD5:
        print("[ERROR] input is not the 1.27.0.52240 original Game.dll (MD5=%s)" % h)
        sys.exit(1)
    for rva, old, new in PATCHES:
        off = rva_to_offset(bytes(data), rva)
        if bytes(data[off:off + len(old)]) != old:
            print("[ERROR] bytes at RVA %08X (off %08X) do not match expected original" % (rva, off))
            sys.exit(1)
        data[off:off + len(new)] = new
    h = md5hex(bytes(data))
    if h != PATCHED_MD5:
        print("[ERROR] patched MD5 mismatch: %s" % h)
        sys.exit(1)
    open(dst, "wb").write(bytes(data))
    print("[OK] wrote %s (MD5 %s)" % (dst, h))


if __name__ == "__main__":
    main()
