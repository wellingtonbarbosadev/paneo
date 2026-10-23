#!/usr/bin/env python3
"""Converte o PNG aprovado em ICNS e ICO, usando sips/iconutil no macOS."""
from pathlib import Path
import struct
import subprocess
import tempfile

root = Path(__file__).resolve().parent
source = root / "paneo-logo-v2-minimal.png"


def resize(size, target):
    subprocess.run(
        ["sips", "-z", str(size), str(size), str(source), "--out", str(target)],
        check=True, stdout=subprocess.DEVNULL,
    )


with tempfile.TemporaryDirectory(prefix="paneo-icons-") as temporary:
    work = Path(temporary)
    iconset = work / "Paneo.iconset"
    iconset.mkdir()
    for size in (16, 32, 128, 256, 512):
        resize(size, iconset / f"icon_{size}x{size}.png")
        resize(size * 2, iconset / f"icon_{size}x{size}@2x.png")
    subprocess.run(
        ["iconutil", "-c", "icns", str(iconset), "-o", str(root / "Paneo.icns")],
        check=True,
    )
    sizes = (16, 24, 32, 48, 64, 128, 256)
    entries, payloads = [], []
    offset = 6 + 16 * len(sizes)
    for size in sizes:
        target = work / f"windows-{size}.png"
        resize(size, target)
        data = target.read_bytes()
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256,
                                   0, 0, 1, 32, len(data), offset))
        payloads.append(data)
        offset += len(data)
    (root / "paneo.ico").write_bytes(
        struct.pack("<HHH", 0, 1, len(sizes)) + b"".join(entries + payloads)
    )
