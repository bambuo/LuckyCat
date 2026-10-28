#!/usr/bin/env bash
# 构建 web 与桌面程序，并用 Velopack 生成安装包。
# 用法：RID=win-x64|osx-arm64|linux-x64 ./scripts/package-desktop.sh [--upload]
# --upload 会创建 GitHub Release，需要 GH_TOKEN。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${RID:?set RID=win-x64|osx-arm64|linux-x64}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
[[ -n "$VERSION" ]] || { echo "cannot read <Version> from Directory.Build.props" >&2; exit 1; }

UPLOAD=0
if [[ "${1:-}" == "--upload" ]]; then
  UPLOAD=1
  : "${GH_TOKEN:?set GH_TOKEN before --upload}"
fi

PUBLISH="$ROOT/artifacts/$RID"
ASSETS="$ROOT/desktop/LuckyCat.Desktop/Assets"
ICON="$ASSETS/app.png"
MAIN_EXE="LuckyCat.Desktop"
PACK_ARGS=()
MISSING=()

case "$RID" in
  win-x64)   CHANNEL=win;   ICON="$ASSETS/app.ico"; MAIN_EXE="LuckyCat.Desktop.exe" ;;
  osx-arm64) CHANNEL=osx;   ICON="$ASSETS/app.icns" ;;
  linux-x64) CHANNEL=linux ;;
  *) echo "unsupported RID: $RID" >&2; exit 1 ;;
esac

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$HOME/.dotnet/tools:$DOTNET_ROOT:$PATH"

command -v vpk >/dev/null || { echo "missing vpk: dotnet tool install -g vpk --version 1.3.39-g8d2844f" >&2; exit 1; }
[[ -f "$ICON" ]] || { echo "missing icon: $ICON (run ./scripts/make-icons.sh first)" >&2; exit 1; }

# vpk 1.3.39 仍不提供 --framework/--categories；缺少 spec 要求的元数据时硬失败
VPK_HELP="$(vpk pack -H 2>&1 || true)"
case "$RID" in
  win-x64)
    if grep -q -- '--framework' <<<"$VPK_HELP"; then
      PACK_ARGS+=(--framework webview2)
    else
      MISSING+=("--framework webview2（Windows 安装器声明 WebView2 依赖）")
    fi
    ;;
  linux-x64)
    if grep -q -- '--categories' <<<"$VPK_HELP"; then
      PACK_ARGS+=(--categories Finance)
    else
      MISSING+=("--categories Finance（AppImage 分类）")
    fi
    ;;
esac

if (( ${#MISSING[@]} > 0 )); then
  if [[ "${LUCKYCAT_ALLOW_MISSING_WEBVIEW2:-}" == "1" ]]; then
    echo "警告：当前 vpk 不支持 ${MISSING[*]}，产物将缺少该元数据，仅用于临时验证。" >&2
  else
    echo "错误：当前 vpk 不支持 ${MISSING[*]}。" >&2
    echo "错误：请升级 vpk；确认可接受后设 LUCKYCAT_ALLOW_MISSING_WEBVIEW2=1 继续。" >&2
    exit 1
  fi
fi

(cd "$ROOT/web" && bun install && bun run build)
rm -rf "$PUBLISH"
dotnet publish "$ROOT/desktop/LuckyCat.Desktop/LuckyCat.Desktop.csproj" \
  -c Release -r "$RID" --self-contained -p:PublishSingleFile=false -o "$PUBLISH"

vpk pack -u LuckyCat -v "$VERSION" -p "$PUBLISH" -o "$ROOT/artifacts/releases" \
  -c "$CHANNEL" --mainExe "$MAIN_EXE" --packTitle "招财猫" --icon "$ICON" ${PACK_ARGS[@]+"${PACK_ARGS[@]}"}

if (( UPLOAD )); then
  UPLOAD_ARGS=(upload github --repoUrl https://github.com/bambuo/LuckyCat --token "$GH_TOKEN" \
    --outputDir "$ROOT/artifacts/releases" --channel "$CHANNEL" --publish)
  # 同一版本的多平台发布需要 --merge 合并到已有 Release
  if vpk upload github -H 2>&1 | grep -q -- '--merge'; then UPLOAD_ARGS+=(--merge); fi
  vpk "${UPLOAD_ARGS[@]}"
fi
