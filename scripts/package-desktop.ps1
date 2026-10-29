# 构建 web 与桌面程序，并用 Velopack 生成 Windows 安装包。
# 用法：./scripts/package-desktop.ps1 [-Upload]
# -Upload 会创建 GitHub Release，需要 $env:GH_TOKEN。
param([switch]$Upload)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Assert-NativeSuccess([string]$Step) {
  if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine("error: $Step failed with exit code $LASTEXITCODE")
    exit 1
  }
}

$Root = Split-Path $PSScriptRoot -Parent
$Version = ([xml](Get-Content "$Root/Directory.Build.props")).Project.PropertyGroup.Version
if (-not $Version) { throw 'cannot read <Version> from Directory.Build.props' }

if ($Upload -and [string]::IsNullOrEmpty($env:GH_TOKEN)) {
  [Console]::Error.WriteLine('error: set GH_TOKEN before using -Upload')
  exit 1
}

$Publish = "$Root\artifacts\win-x64"
$Assets = "$Root\desktop\LuckyCat.Desktop\Assets"
$Icon = "$Assets\app.ico"
if (-not (Test-Path $Icon)) { throw "missing icon: $Icon (run scripts/make-icons.sh first)" }

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
  [Console]::Error.WriteLine('error: missing vpk: dotnet tool install -g vpk --version 1.3.39-g8d2844f')
  exit 1
}

Push-Location "$Root\web"
try {
  bun install
  Assert-NativeSuccess 'bun install'
  bun run build
  Assert-NativeSuccess 'bun run build'
} finally { Pop-Location }

Remove-Item $Publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$Root\desktop\LuckyCat.Desktop\LuckyCat.Desktop.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o $Publish
Assert-NativeSuccess 'dotnet publish'

$packArgs = @(
  'pack', '-u', 'bambuo.LuckyCat', '-v', $Version, '-p', $Publish, '-o', "$Root\artifacts\releases",
  '-c', 'win', '--mainExe', 'LuckyCat.Desktop.exe', '--packTitle', '招财猫', '--icon', $Icon
)
# vpk 1.3.39 仍不提供 --framework；缺少 spec 要求的元数据时硬失败
$vpkHelp = (vpk pack -H 2>&1 | Out-String)
if ($vpkHelp -match '--framework') {
  $packArgs += @('--framework', 'webview2')
} elseif ($env:LUCKYCAT_ALLOW_MISSING_WEBVIEW2 -eq '1') {
  Write-Warning '当前 vpk 不支持 --framework webview2，产物将缺少 WebView2 依赖声明，仅用于临时验证'
} else {
  [Console]::Error.WriteLine('error: 当前 vpk 不支持 --framework webview2（Windows 安装器无法声明 WebView2 依赖）')
  [Console]::Error.WriteLine('error: 请升级 vpk；确认可接受后设 $env:LUCKYCAT_ALLOW_MISSING_WEBVIEW2=1 继续')
  exit 1
}
vpk @packArgs
Assert-NativeSuccess 'vpk pack'

if ($Upload) {
  $uploadArgs = @(
    'upload', 'github', '--repoUrl', 'https://github.com/bambuo/LuckyCat', '--token', $env:GH_TOKEN,
    '--outputDir', "$Root\artifacts\releases", '--channel', 'win', '--publish'
  )
  # 同一版本的多平台发布需要 --merge 合并到已有 Release
  if ((vpk upload github -H 2>&1 | Out-String) -match '--merge') { $uploadArgs += '--merge' }
  vpk @uploadArgs
  Assert-NativeSuccess 'vpk upload github'
}

exit 0
