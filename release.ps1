param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = '',
    [string]$GameDir = '',
    [string]$Owner = 'setter6666',
    [string]$Repo = 'CasualtiesUnknown-ItemPicker',
    [string]$Token = $env:GH_TOKEN,
    [switch]$NoPush,
    [switch]$SkipBuild,
    [string]$Root = $PSScriptRoot
)

# 一条命令发版：
#   1) 改版本号（src/ItemPicker.cs 的 BepInPlugin + 说明.txt）
#   2) 本地编译 dist/ItemPicker.dll（需要游戏 DLL）
#   3) 通过 GitHub API 提交到 main 并创建 tag
#   4) GitHub Actions 接管：自动打包 + 发 Release
#
# 用法：
#   $env:GH_TOKEN = 'github_pat_...'
#   ./release.ps1 -Version 1.0.4 -Notes "修复 xxx"
#   ./release.ps1 -Version 1.0.4 -NoPush          # 只改版本 + 编译，不提交

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = (Get-Location).Path }
if (-not $Version -match '^\d+\.\d+\.\d+$') { throw "版本号格式应为 x.y.z，例如 1.0.4" }
$Tag = "v$Version"

if (-not $GameDir) {
    $candidates = @(
        'C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown',
        'D:\SteamLibrary\steamapps\common\Casualties Unknown',
        'E:\SteamLibrary\steamapps\common\Casualties Unknown',
        'F:\SteamLibrary\steamapps\common\Casualties Unknown'
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'CasualtiesUnknown_Data\Managed\Assembly-CSharp.dll')) { $GameDir = $c; break }
    }
}
if (-not $GameDir) { throw "找不到游戏目录，请用 -GameDir 指定（包含 CasualtiesUnknown.exe 的那层）" }

# --- 1) 改版本号 ---------------------------------------------------------
$srcPath = Join-Path $Root 'src\ItemPicker.cs'
$src = [IO.File]::ReadAllText($srcPath)
$pattern = '\[BepInPlugin\("qol\.itempicker",\s*"Item Picker",\s*"[0-9]+\.[0-9]+\.[0-9]+"\)\]'
if ($src -notmatch $pattern) { throw "在 src/ItemPicker.cs 里找不到 BepInPlugin 版本号" }
$src = [regex]::Replace($src, $pattern, "[BepInPlugin(""qol.itempicker"", ""Item Picker"", ""$Version"")]")
[IO.File]::WriteAllText($srcPath, $src, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "版本号 -> $Version（src/ItemPicker.cs）"

$readmePath = Join-Path $Root '说明.txt'
if (Test-Path $readmePath) {
    $txt = [IO.File]::ReadAllText($readmePath)
    $txt = [regex]::Replace($txt, '(版本：)[^\r\n]+', "`${1}$Version")
    [IO.File]::WriteAllText($readmePath, $txt, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "版本号 -> $Version（说明.txt）"
}

# --- 2) 编译 -------------------------------------------------------------
$dllPath = Join-Path $Root 'dist\ItemPicker.dll'
if (-not $SkipBuild) {
    # 用 scriptblock 执行，兼容「禁止运行 .ps1」的执行策略
    $buildScript = [IO.File]::ReadAllText((Join-Path $Root 'build.ps1'))
    & ([scriptblock]::Create($buildScript)) -GameDir $GameDir -Out $dllPath -Source $srcPath | Write-Host
    if (-not (Test-Path $dllPath)) { throw "编译失败：没有生成 $dllPath" }
} else {
    Write-Host "跳过编译，使用现有 dist/ItemPicker.dll"
}

if ($NoPush) {
    Write-Host ""
    Write-Host "已跳过提交（-NoPush）。本地产物：$dllPath"
    return
}

# --- 3) 提交 + 打 tag ----------------------------------------------------
if (-not $Token) { throw "缺少 token：请设置环境变量 GH_TOKEN，或传 -Token" }
$h = @{
    Authorization          = "Bearer $Token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'itempicker-release'
}
$api = "https://api.github.com/repos/$Owner/$Repo"
function Post-Json($uri, $body) {
    Invoke-RestMethod -Uri $uri -Headers $h -Method Post -Body ($body | ConvertTo-Json -Depth 10 -Compress) -ContentType 'application/json; charset=utf-8'
}

$head = Invoke-RestMethod -Uri "$api/git/ref/heads/main" -Headers $h
$headCommit = Invoke-RestMethod -Uri "$api/git/commits/$($head.object.sha)" -Headers $h

$files = @(
    @{ Path = 'src/ItemPicker.cs'; File = $srcPath },
    @{ Path = 'dist/ItemPicker.dll'; File = $dllPath }
)
if (Test-Path $readmePath) { $files += @{ Path = '说明.txt'; File = $readmePath } }

$entries = New-Object System.Collections.ArrayList
foreach ($f in $files) {
    $bytes = [IO.File]::ReadAllBytes($f.File)
    $blob = Post-Json "$api/git/blobs" @{ content = [Convert]::ToBase64String($bytes); encoding = 'base64' }
    [void]$entries.Add(@{ path = $f.Path; mode = '100644'; type = 'blob'; sha = $blob.sha })
    Write-Host ("上传 {0} ({1} B)" -f $f.Path, $bytes.Length)
}

$tree = Post-Json "$api/git/trees" @{ base_tree = $headCommit.tree.sha; tree = $entries.ToArray() }
$msg = "release: $Tag"
if ($Notes) { $msg += "`n`n$Notes" }
$commit = Post-Json "$api/git/commits" @{ message = $msg; tree = $tree.sha; parents = @($head.object.sha) }
Invoke-RestMethod -Uri "$api/git/refs/heads/main" -Headers $h -Method Patch `
    -Body (@{ sha = $commit.sha; force = $false } | ConvertTo-Json) -ContentType 'application/json; charset=utf-8' | Out-Null
Write-Host "已提交：$($commit.sha.Substring(0,7))"

try {
    Invoke-RestMethod -Uri "$api/git/refs" -Headers $h -Method Post `
        -Body (@{ ref = "refs/tags/$Tag"; sha = $commit.sha } | ConvertTo-Json) -ContentType 'application/json; charset=utf-8' | Out-Null
    Write-Host "已创建 tag：$Tag"
} catch {
    throw "创建 tag 失败（可能已存在）：$($_.Exception.Message)"
}

Write-Host ""
Write-Host "完成。GitHub Actions 会自动打包并发布："
Write-Host "  https://github.com/$Owner/$Repo/actions"
Write-Host "  https://github.com/$Owner/$Repo/releases/tag/$Tag"
