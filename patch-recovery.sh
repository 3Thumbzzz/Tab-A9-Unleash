#!/usr/bin/env bash
# Builds an Odin-flashable fastbootd recovery (+ verification-disabled vbmeta)
# for the Samsung Galaxy Tab A9 SM-X110 from your own stock firmware.
# Patching is done by fastboot-patcher (https://github.com/ratcoded/fastboot-patcher),
# itself based on Johx22/Patch-Recovery and phhusson's samsung-galaxy-a51-gsi-boot.
# Run via patch-recovery.bat (WSL), from the folder holding the AP_ and BL_ files.

set -euo pipefail

HERE="$(pwd)"
WORK="$HOME/x110-fastbootd"
OUT="$HERE/output"
REPOS=(
  "https://github.com/ratcoded/fastboot-patcher.git"
  "https://github.com/Ch3nYu/fastboot-patcher.git"
)

die() { echo; echo "ERROR: $*"; exit 1; }
step() { echo; echo "==> $*"; }

shopt -s nullglob
APS=(AP_*.tar.md5 AP_*.tar)
BLS=(BL_*.tar.md5 BL_*.tar)
shopt -u nullglob
[ ${#APS[@]} -ge 1 ] || die "No AP_*.tar.md5 file found in $HERE"
AP="$HERE/${APS[0]}"
BL=""
[ ${#BLS[@]} -ge 1 ] && BL="$HERE/${BLS[0]}"
echo "AP file: ${APS[0]}"
[ -n "$BL" ] && echo "BL file: ${BLS[0]}"

if [ -z "${SKIP_APT:-}" ]; then
  step "Installing tools inside WSL (may ask for your WSL password)"
  sudo apt-get update -qq
  sudo apt-get install -y -qq git wget lz4 tar openssl python3 >/dev/null
fi

step "Preparing work folder $WORK"
rm -rf "$WORK"
mkdir -p "$WORK"

step "Extracting recovery.img.lz4 from AP (large file, takes a minute)"
tar -xf "$AP" -C "$WORK" recovery.img.lz4 2>/dev/null || true
[ -f "$WORK/recovery.img.lz4" ] || die "recovery.img.lz4 not found inside ${APS[0]}"

step "Extracting vbmeta.img.lz4 (BL first, then AP)"
if [ -n "$BL" ]; then tar -xf "$BL" -C "$WORK" vbmeta.img.lz4 2>/dev/null || true; fi
if [ ! -f "$WORK/vbmeta.img.lz4" ]; then tar -xf "$AP" -C "$WORK" vbmeta.img.lz4 2>/dev/null || true; fi
[ -f "$WORK/vbmeta.img.lz4" ] || die "vbmeta.img.lz4 not found in BL or AP"
lz4 -d -f -q "$WORK/vbmeta.img.lz4" "$WORK/vbmeta.img"

step "Downloading fastboot-patcher"
CLONED=""
for r in "${REPOS[@]}"; do
  if git clone -q --depth 1 "$r" "$WORK/fastboot-patcher" 2>/dev/null; then CLONED="$r"; break; fi
done
[ -n "$CLONED" ] || die "Could not clone fastboot-patcher from GitHub"
echo "Using $CLONED"

step "Patching recovery for fastbootd"
cd "$WORK/fastboot-patcher"
cp "$WORK/recovery.img.lz4" .
chmod +x ./patcher 2>/dev/null || true
find . -maxdepth 2 -name 'magiskboot*' -exec chmod +x {} \; 2>/dev/null || true
# Run as root (the app starts this with wsl -u root) so the patcher's own
# "sudo" cleanup never waits for a password. Keep going even if cleanup fails.
./patcher || echo "(patcher exited non-zero; checking for its output anyway)"

# Find the patched image: output.img, else pull it out of output.tar.
PATCHED=""
for f in ./output.img $(ls -t ./*.img 2>/dev/null | grep -v -E '/(recovery|raw|header)\.img$'); do
  if [ -s "$f" ]; then PATCHED="$f"; break; fi
done
if [ -z "$PATCHED" ] && [ -s ./output.tar ]; then
  mkdir -p ./fromtar
  tar -xf ./output.tar -C ./fromtar 2>/dev/null || true
  PATCHED="$(find ./fromtar -type f -name '*.img' -size +1M | head -n 1)"
fi
[ -n "$PATCHED" ] && [ -s "$PATCHED" ] || die "Patcher finished but no patched image was found. Check the output above."
PATCHED="$(readlink -f "$PATCHED")"
echo "Patched image: $(basename "$PATCHED")"

step "Disabling verification in vbmeta (AVB flags = 3)"
python3 - "$WORK/vbmeta.img" <<'PY'
import sys
p = sys.argv[1]
b = bytearray(open(p, "rb").read())
if b[:4] != b"AVB0":
    sys.exit("vbmeta.img has no AVB0 header")
b[123] |= 0x03  # flags field, bytes 120-123 big-endian: verity + verification disabled
open(p, "wb").write(b)
print("vbmeta flags set to", int.from_bytes(b[120:124], "big"))
PY

step "Packing Odin tar (recovery.img + vbmeta.img)"
PACK="$WORK/pack"
mkdir -p "$PACK"
cp "$PATCHED" "$PACK/recovery.img"
cp "$WORK/vbmeta.img" "$PACK/vbmeta.img"
cd "$PACK"
NAME="SM-X110-fastbootd-recovery.tar"
tar -H ustar -cf "$NAME" recovery.img vbmeta.img
md5sum -t "$NAME" >> "$NAME"
mv "$NAME" "$NAME.md5"

mkdir -p "$OUT"
cp "$PACK/$NAME.md5" "$OUT/"
cp "$PATCHED" "$OUT/recovery-patched.img"
cp "$WORK/vbmeta.img" "$OUT/vbmeta-disabled.img"

step "Done"
echo "Flash this in Odin's AP slot (Auto Reboot unticked):"
echo "  output/$NAME.md5"
