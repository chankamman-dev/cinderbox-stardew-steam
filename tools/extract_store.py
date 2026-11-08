"""从 .NET for Android 的 libassembly-store.so 里拆出程序集（XALZ = LZ4 压缩块）。

用法：python -I extract_store.py <libassembly-store.so> <输出目录>
只按 PE 元数据里的程序集名命名，不执行任何拆出的代码。
"""
import os
import re
import struct
import sys


def lz4_block(src, size):
    out = bytearray()
    i = 0
    n = len(src)
    while i < n and len(out) < size:
        tok = src[i]; i += 1
        lit = tok >> 4
        if lit == 15:
            while True:
                b = src[i]; i += 1; lit += b
                if b != 255:
                    break
        out += src[i:i + lit]; i += lit
        if len(out) >= size or i >= n:
            break
        off = src[i] | (src[i + 1] << 8); i += 2
        ml = tok & 15
        if ml == 15:
            while True:
                b = src[i]; i += 1; ml += b
                if b != 255:
                    break
        ml += 4
        start = len(out) - off
        for k in range(ml):
            out.append(out[start + k])
    return bytes(out[:size])


def asm_name(pe):
    # 粗略取 #Strings 堆中 .dll 名前面的模块名：用文件内首个 "xxx.dll" 字符串
    m = re.search(rb"([A-Za-z0-9_.\-]+)\.dll\x00", pe)
    return m.group(1).decode() if m else None


def main():
    store, outdir = sys.argv[1], sys.argv[2]
    os.makedirs(outdir, exist_ok=True)
    d = open(store, "rb").read()
    offs = [m.start() for m in re.finditer(b"XALZ", d)]
    offs.append(len(d))
    for idx in range(len(offs) - 1):
        o = offs[idx]
        _, index, size = struct.unpack_from("<4sII", d, o)
        try:
            pe = lz4_block(d[o + 12:offs[idx + 1]], size)
        except Exception as e:  # noqa
            print("fail", o, e)
            continue
        name = asm_name(pe) or f"asm_{index}"
        path = os.path.join(outdir, f"{name}.dll")
        if os.path.exists(path):
            path = os.path.join(outdir, f"{name}.{index}.dll")
        open(path, "wb").write(pe)
    print(len(offs) - 1, "assemblies")


if __name__ == "__main__":
    main()
