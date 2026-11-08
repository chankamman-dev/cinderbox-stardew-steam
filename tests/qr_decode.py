"""把 CoreSmoke qr 写出的 0/1 矩阵渲染成图片，用 OpenCV 解码，和原文比对。
用法：python -I qr_decode.py <目录> <原文1> <原文2> ...
"""
import os
import sys

import cv2
import numpy as np

d = sys.argv[1]
ok = True
for i, expected in enumerate(sys.argv[2:]):
    rows = open(os.path.join(d, f"qr{i}.txt")).read().split()
    m = np.array([[1 if c == "1" else 0 for c in r] for r in rows], dtype=np.uint8)
    img = np.where(m == 1, 0, 255).astype(np.uint8)
    img = np.pad(img, 4, constant_values=255)
    img = cv2.resize(img, None, fx=8, fy=8, interpolation=cv2.INTER_NEAREST)
    cv2.imwrite(os.path.join(d, f"qr{i}.png"), img)
    text, pts, _ = cv2.QRCodeDetector().detectAndDecode(img)
    good = text == expected
    ok &= good
    print(f"qr{i} size {m.shape[0]}: {'OK' if good else 'FAIL'} -> {text!r}")
sys.exit(0 if ok else 1)
