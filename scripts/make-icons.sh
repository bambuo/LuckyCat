#!/usr/bin/env bash
# 从 web/public/logo.svg 生成桌面图标：app.png（1024）、app.icns 与 ICO 所需的多尺寸 PNG。
# 仅 macOS：依赖 Google Chrome（无头截图）、sips、iconutil 与 node。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/desktop/LuckyCat.Desktop/Assets"
SRC="$ROOT/web/public/logo.svg"
CHROME="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
ICO_SIZES=(16 32 48 64 128 256)

for bin in sips iconutil node; do
  command -v "$bin" >/dev/null || { echo "missing command: $bin" >&2; exit 1; }
done
[[ -x "$CHROME" ]] || { echo "missing Chrome: $CHROME" >&2; exit 1; }
[[ -f "$SRC" ]] || { echo "missing source svg: $SRC" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"

cat > "$WORK/icon.html" <<HTML
<!doctype html><html><body style="margin:0;background:transparent">
<img src="file://$SRC" style="width:1024px;height:1024px;display:block">
</body></html>
HTML

"$CHROME" --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 \
  --user-data-dir="$WORK/chrome-profile" \
  --screenshot="$WORK/app-1024.png" --window-size=1024,1024 "file://$WORK/icon.html" >/dev/null 2>&1
[[ -f "$WORK/app-1024.png" ]] || { echo "Chrome did not write a screenshot" >&2; exit 1; }

cp "$WORK/app-1024.png" "$OUT/app.png"

for size in "${ICO_SIZES[@]}"; do
  sips -z "$size" "$size" "$WORK/app-1024.png" --out "$WORK/app-$size.png" >/dev/null
done

ICONSET="$WORK/app.iconset"
mkdir -p "$ICONSET"
for spec in "16 icon_16x16" "32 icon_16x16@2x" "32 icon_32x32" "64 icon_32x32@2x" \
            "128 icon_128x128" "256 icon_128x128@2x" "256 icon_256x256" "512 icon_256x256@2x" \
            "512 icon_512x512" "1024 icon_512x512@2x"; do
  set -- $spec
  sips -z "$1" "$1" "$WORK/app-1024.png" --out "$ICONSET/$2.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$OUT/app.icns"

node "$ROOT/scripts/make-ico.mjs" "$WORK" "$OUT/app.ico" "${ICO_SIZES[@]}"
echo "icons written to $OUT"
