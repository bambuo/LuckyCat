#!/usr/bin/env bash
# 把 vpk 生成的安装器/便携包/AppImage 统一改为带版本号的命名，并同步 feed。
# 用法：rename-artifacts.sh <version> <releases-dir> <channel>
# vpk 的 nupkg 已带版本，保持不动；这里只改 pkg / zip / AppImage 与 assets/releases 中的引用。
set -euo pipefail

VERSION="${1:?usage: rename-artifacts.sh <version> <dir> <channel>}"
DIR="${2:?}"
CHANNEL="${3:?}"

cd "$DIR"

old_names=(
  "bambuo.LuckyCat-${CHANNEL}-Setup.pkg"
  "bambuo.LuckyCat-${CHANNEL}-Portable.zip"
  "bambuo.LuckyCat-${CHANNEL}.AppImage"
)
new_names=(
  "bambuo.LuckyCat-${VERSION}-${CHANNEL}-Setup.pkg"
  "bambuo.LuckyCat-${VERSION}-${CHANNEL}-Portable.zip"
  "bambuo.LuckyCat-${VERSION}-${CHANNEL}.AppImage"
)

# vpk 对“默认” channel（linux）省略 channel 后缀；只有该 channel 才处理无后缀名。
if [[ "$CHANNEL" == "linux" ]]; then
  old_names+=("bambuo.LuckyCat.AppImage")
  new_names+=("bambuo.LuckyCat-${VERSION}-${CHANNEL}.AppImage")
fi

for i in "${!old_names[@]}"; do
  old="${old_names[$i]}"
  new="${new_names[$i]}"
  [[ -f "$old" ]] || continue
  mv "$old" "$new"

  for feed in "assets.${CHANNEL}.json" "releases.${CHANNEL}.json"; do
    [[ -f "$feed" ]] || continue
    OLD="$old" NEW="$new" perl -0pi -e 's/\Q$ENV{OLD}\E/$ENV{NEW}/g' "$feed"
  done
done

echo "renamed artifacts in $DIR for channel $CHANNEL (version $VERSION)"
