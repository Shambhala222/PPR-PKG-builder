"""Leave Finder AppleDouble / .DS_Store out of the bundled Fix 12 GP5 walk.

Windows dumps do not have these files. The Mac wrapper skips them so they are
not packed. Idempotent.
"""
from __future__ import annotations

import sys
from pathlib import Path

MARKER = "def is_macos_host_artifact"
HELPER = '''
def is_macos_host_artifact(name: str) -> bool:
    """Finder AppleDouble, .DS_Store, and __MACOSX. Not present on Windows dumps."""
    if name.startswith("._") or name == "__MACOSX":
        return True
    return name.casefold() == ".ds_store"


'''
AFTER_RELATIVE = '''            relative = relative_posix(root, item)
            if is_macos_host_artifact(item.name):
                skipped.append(relative + ("/" if item.is_dir() else "") + " (macOS host file)")
                continue
'''
ORIGINAL_RELATIVE = "            relative = relative_posix(root, item)\n"
DDS_AFTER = '''        if not dds.is_file() or dds.suffix.casefold() != ".dds":
            continue
        if is_macos_host_artifact(dds.name):
            continue
'''
DDS_ORIGINAL = '''        if not dds.is_file() or dds.suffix.casefold() != ".dds":
            continue
'''


def apply(path: Path) -> None:
    text = path.read_text(encoding="utf-8")
    if MARKER not in text:
        needle = "def collect_files("
        if needle not in text:
            raise SystemExit(f"collect_files not found in {path}")
        text = text.replace(needle, HELPER + needle, 1)
    if "if is_macos_host_artifact(item.name):" not in text:
        # First relative_posix in collect_files walk.
        idx = text.find("def collect_files(")
        rel = text.find(ORIGINAL_RELATIVE, idx)
        if rel < 0:
            raise SystemExit(f"relative_posix assignment not found in {path}")
        text = text[:rel] + AFTER_RELATIVE + text[rel + len(ORIGINAL_RELATIVE):]
    if "is_macos_host_artifact(dds.name)" not in text:
        if DDS_ORIGINAL not in text:
            raise SystemExit(f"DDS filter not found in {path}")
        text = text.replace(DDS_ORIGINAL, DDS_AFTER, 1)
    path.write_text(text, encoding="utf-8")
    print("macos host skip applied", path)


if __name__ == "__main__":
    apply(Path(sys.argv[1]))
