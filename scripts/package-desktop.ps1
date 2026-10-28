# 构建 web 与桌面程序，并用 Velopack 生成 Windows 安装包。
# 用法：./scripts/package-desktop.ps1 [-Upload]
# -Upload 会创建 GitHub Release，需要 $env:GH_TOKEN。
param([switch]$Upload)

$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$Version = ([xml](Get-Content "$Root/Directory.Build.props")).Project.PropertyGroup.Version
if (-not $Version) { throw 'cannot read <Version> from Directory.Build.props' }

$Publish = "$Root\artifacts\win-x64"
$Assets = "$Root\desktop\LuckyCat.Desktop\Assets"
$Icon = "$Assets\app.ico"
if (-not (Test-Path $Icon)) { throw "missing icon: $Icon (run scripts/make-icons.sh first)" }

Push-Location "$Root\web"
try { bun install; bun run build } finally { Pop-Location }

Remove-Item $Publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$Root\desktop\LuckyCat.Desktop\LuckyCat.Desktop.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o $Publish

$packArgs = @(
  'pack', '-u', 'LuckyCat', '-v', $Version, '-p', $Publish, '-o', "$Root\artifacts\releases",
  '--mainExe', 'LuckyCat.Desktop.exe', '--packTitle', '招财猫', '--icon', $Icon
)
# vpk 1.2.161 未提供 --framework，仅在当前 CLI 支持时才追加
if ((vpk pack -H 2>&1) -match '--framework') { $packArgs += @('--framework', 'webview2') }
vpk @packArgs

if ($Upload) {
  vpk upload github --repoUrl https://github.com/bambuo/LuckyCat --token $env:GH_TOKEN `
    --outputDir "$Root\artifacts\releases" --publish
}
