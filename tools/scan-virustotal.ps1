param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$ApiKey = $env:VT_API_KEY,
    [string]$Owner = 'setter6666',
    [string]$Repo = 'CasualtiesUnknown-ItemPicker',
    [string]$Tag = '',
    [string]$GhToken = $env:GH_TOKEN,
    [switch]$PatchRelease
)

# 用 VirusTotal API 扫描发布包，并可把结果写回 GitHub Release 说明。
# 用法：
#   $env:VT_API_KEY = '...'
#   ./scan-virustotal.ps1 -File .\ItemPicker-v1.0.4.zip -Tag v1.0.4 -PatchRelease

$ErrorActionPreference = 'Stop'
if (-not $ApiKey) { throw '缺少 VT API key：请设置 $env:VT_API_KEY' }
if (-not (Test-Path $File)) { throw "文件不存在：$File" }

Add-Type -AssemblyName System.Net.Http

$filePath = (Resolve-Path $File).Path
$fileName = [IO.Path]::GetFileName($filePath)
$sha256 = (Get-FileHash $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "文件：$fileName"
Write-Host "SHA256：$sha256"

$client = [System.Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.Add('x-apikey', $ApiKey)

# 先查是否已有报告
$existing = $null
try {
    $r = $client.GetAsync("https://www.virustotal.com/api/v3/files/$sha256").Result
    if ($r.IsSuccessStatusCode) {
        $existing = $r.Content.ReadAsStringAsync().Result | ConvertFrom-Json
        Write-Host "VirusTotal 已有该文件的分析结果，直接使用。"
    }
} catch { }

if (-not $existing) {
    Write-Host "上传到 VirusTotal ..."
    $multipart = [System.Net.Http.MultipartFormDataContent]::new()
    $bytes = [IO.File]::ReadAllBytes($filePath)
    $fileContent = [System.Net.Http.ByteArrayContent]::new([byte[]]$bytes)
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/octet-stream')
    $multipart.Add($fileContent, 'file', $fileName)
    $resp = $client.PostAsync('https://www.virustotal.com/api/v3/files', $multipart).Result
    $body = $resp.Content.ReadAsStringAsync().Result
    if (-not $resp.IsSuccessStatusCode) { throw "上传失败（$([int]$resp.StatusCode)）：$body" }
    $upload = $body | ConvertFrom-Json
    $analysisId = $upload.data.id
    Write-Host "分析任务：$analysisId，等待结果 ..."

    $stats = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 8
        $a = ($client.GetAsync("https://www.virustotal.com/api/v3/analyses/$analysisId").Result.Content.ReadAsStringAsync().Result) | ConvertFrom-Json
        $status = $a.data.attributes.status
        Write-Host "  状态：$status"
        if ($status -eq 'completed') { $stats = $a.data.attributes.stats; break }
    }
    if (-not $stats) { throw '等待分析结果超时' }
} else {
    $stats = $existing.data.attributes.last_analysis_stats
}

$link = "https://www.virustotal.com/gui/file/$sha256"
$summary = "{0} 引擎检出 {1} 个恶意 / {2} 个可疑（共 {3} 个引擎）" -f $fileName, $stats.malicious, $stats.suspicious, ($stats.malicious + $stats.suspicious + $stats.undetected + $stats.harmless)
Write-Host ""
Write-Host $summary
Write-Host "链接：$link"

if ($PatchRelease -and $Tag) {
    if (-not $GhToken) { throw '需要 GH_TOKEN 才能写回 Release' }
    $h = @{ Authorization = "Bearer $GhToken"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'vt-scan' }
    $api = "https://api.github.com/repos/$Owner/$Repo"
    $rel = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Headers $h
    $body = $rel.body
    $section = "`n## 病毒检测`n`n[VirusTotal 报告（$($stats.malicious) 检出）]($link)`n`n> $(($stats.malicious + $stats.suspicious + $stats.undetected + $stats.harmless)) 个引擎扫描，恶意 $($stats.malicious) / 可疑 $($stats.suspicious)。SHA256 \`$sha256\`。`n"
    if ($body -match '## 病毒检测') {
        $body = [regex]::Replace($body, '## 病毒检测[\s\S]*$', $section.TrimStart())
    } else {
        $body = $body + $section
    }
    Invoke-RestMethod -Uri "$api/releases/$($rel.id)" -Headers $h -Method Patch -Body (@{ body = $body } | ConvertTo-Json) -ContentType 'application/json; charset=utf-8' | Out-Null
    Write-Host "已写回 Release $Tag 的说明。"
}
