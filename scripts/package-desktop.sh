#!/usr/bin/env bash
# 构建 web 与桌面程序，并用 Velopack 生成安装包。
# 用法：RID=win-x64|osx-arm64|linux-x64 ./scripts/package-desktop.sh [--upload]
# --upload 会创建 GitHub Release，需要 GH_TOKEN。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${RID:?set RID=win-x64|osx-arm64|linux-x64}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
[[ -n "$VERSION" ]] || { echo "cannot read <Version> from Directory.Build.props" >&2; exit 1; }

PUBLISH="$ROOT/artifacts/$RID"
ASSETS="$ROOT/desktop/LuckyCat.Desktop/Assets"
ICON="$ASSETS/app.png"
MAIN_EXE="LuckyCat.Desktop"
PACK_ARGS=()

case "$RID" in
  win-x64)   ICON="$ASSETS/app.ico"; MAIN_EXE="LuckyCat.Desktop.exe" ;;
  osx-arm64) ICON="$ASSETS/app.icns" ;;
  linux-x64) ;;
  *) echo "unsupported RID: $RID" >&2; exit 1 ;;
esac

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$HOME/.dotnet/tools:$DOTNET_ROOT:$PATH"

command -v vpk >/dev/null || { echo "missing vpk: dotnet tool install -g vpk --version 1.2.161" >&2; exit 1; }
[[ -f "$ICON" ]] || { echo "missing icon: $ICON (run ./scripts/make-icons.sh first)" >&2; exit 1; }

# vpk 1.2.161 未提供 --framework/--categories，仅在当前 CLI 支持时才追加
VPK_HELP="$(vpk pack -H 2>&1 || true)"
case "$RID" in
  win-x64)
    if grep -q -- '--framework' <<<"$VPK_HELP"; then PACK_ARGS+=(--framework webview2); fi
    ;;
  linux-x64)
    if grep -q -- '--categories' <<<"$VPK_HELP"; then PACK_ARGS+=(--categories Finance); fi
    ;;
esac

(cd "$ROOT/web" && bun install && bun run build)
rm -rf "$PUBLISH"
dotnet publish "$ROOT/desktop/LuckyCat.Desktop/LuckyCat.Desktop.csproj" \
  -c Release -r "$RID" --self-contained -p:PublishSingleFile=false -o "$PUBLISH"

vpk pack -u LuckyCat -v "$VERSION" -p "$PUBLISH" -o "$ROOT/artifacts/releases" \
  --mainExe "$MAIN_EXE" --packTitle "招财猫" --icon "$ICON" ${PACK_ARGS[@]+"${PACK_ARGS[@]}"}

if [[ "${1:-}" == "--upload" ]]; then
  vpk upload github --repoUrl https://github.com/bambuo/LuckyCat --token "${GH_TOKEN:?set GH_TOKEN}" \
    --outputDir "$ROOT/artifacts/releases" --publish
fi
