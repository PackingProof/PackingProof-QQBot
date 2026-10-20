# 把当前 tag 的 QQBot 产物一键发布到 GitHub 与 Gitee Release（做法与 PackingProof-Desktop\
# Tools\Publish-Releases.ps1 一致，产物不同而已）。
#
#   pwsh -NoProfile -File Tools\Publish-Releases.ps1 -Tag v0.0.10 [-Title "一句话"] `
#       [-NotesFile <md 文件>] [-Target both|github|gitee] [-ReplaceAssets] [-SkipBuild] [-ValidateOnly]
#
# 约定：
# - 产物由 Tools\Build-Ppext.ps1 生成：独立包 PackingProof-QQBot-<版本>-win-x64.zip（自包含、跨机可用）
#   与市场插件 packingproof.qqbot-<版本>.ppext（框架依赖，由桌面端把共享运行时交给它）；
# - 市场插件必须叫 `<id>-<版本>.ppext`，扩展市场条目里的下载地址就是这个名字；
# - Gitee 令牌固定取仓库根 `.env` 的 GITEE_TOKEN，缺失时回退本机 `~\.gitee\apikey.txt`；
#   只注入环境变量给 CLI，不打印、不落盘；
# - Gitee 不支持覆盖上传：-ReplaceAssets 会先按同名删除旧附件再上传；
# - -ValidateOnly 只校验产物并打印市场条目片段，不发起任何上传；
# - 跑完会打印要填进 PackingProof-Extensions\extensions\packingproof.qqbot\versions\<版本>.json 的片段。
param(
    [string]$Tag = "",
    [string]$Title = "",
    [string]$NotesFile = "",
    [ValidateSet("both", "github", "gitee")]
    [string]$Target = "both",
    [switch]$ReplaceAssets,
    [switch]$SkipBuild,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$repository = "PackingProof/PackingProof-QQBot"

function Assert-Command {
    param([string]$Name, [string]$Hint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "缺少命令 $Name。$Hint"
    }
}

# 令牌只进环境变量：先看当前进程，再看仓库 .env，最后回退本机 apikey.txt（与 AGENTS.md 一致）。
function Import-GiteeToken {
    param([string]$RepositoryRoot)

    if (-not [string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) {
        return "环境变量 GITEE_TOKEN"
    }
    $envFile = Join-Path $RepositoryRoot ".env"
    if (Test-Path -LiteralPath $envFile -PathType Leaf) {
        foreach ($line in Get-Content -Encoding UTF8 -LiteralPath $envFile) {
            if ($line -match "^\s*GITEE_TOKEN\s*=\s*(.*)$") {
                $token = $Matches[1].Trim().Trim('"', "'")
                if (-not [string]::IsNullOrWhiteSpace($token)) {
                    $env:GITEE_TOKEN = $token
                    return ".env 的 GITEE_TOKEN"
                }
            }
        }
    }
    $fallback = Join-Path $HOME ".gitee\apikey.txt"
    if (Test-Path -LiteralPath $fallback -PathType Leaf) {
        $token = (Get-Content -Raw -Encoding UTF8 -LiteralPath $fallback).Trim()
        if (-not [string]::IsNullOrWhiteSpace($token)) {
            $env:GITEE_TOKEN = $token
            return "本机 ~\.gitee\apikey.txt"
        }
    }
    return ""
}

function Get-GiteeReleaseId {
    param([string]$Repo, [string]$ReleaseTag)

    $headers = @{ Authorization = "Bearer $($env:GITEE_TOKEN)" }
    $release = Invoke-RestMethod `
        -Uri "https://gitee.com/api/v5/repos/$Repo/releases/tags/$ReleaseTag" `
        -Headers $headers -Method Get -TimeoutSec 30
    return [long]$release.id
}

# gitee CLI 只能上传、不能删附件；重复上传会在 Release 上留同名文件，所以替换前先按名字删掉旧的。
function Remove-GiteeReleaseAttachmentByName {
    param([string]$Repo, [long]$ReleaseId, [string]$FileName)

    $headers = @{ Authorization = "Bearer $($env:GITEE_TOKEN)" }
    $attachments = Invoke-RestMethod `
        -Uri "https://gitee.com/api/v5/repos/$Repo/releases/$ReleaseId/attach_files" `
        -Headers $headers -Method Get -TimeoutSec 30
    if ($null -eq $attachments) { return 0 }

    $removed = 0
    foreach ($attachment in @($attachments)) {
        if (-not [string]::Equals([string]$attachment.name, $FileName, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        Invoke-RestMethod `
            -Uri "https://gitee.com/api/v5/repos/$Repo/releases/$ReleaseId/attach_files/$($attachment.id)" `
            -Headers $headers -Method Delete -TimeoutSec 30 | Out-Null
        Write-Host "  已删除同名旧附件：$FileName"
        $removed++
    }
    return $removed
}

function Test-ZipHasEntry {
    param([string]$ZipPath, [string]$Name)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        return @($archive.Entries | Where-Object { $_.FullName -eq $Name }).Count -gt 0
    }
    finally { $archive.Dispose() }
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = (& git describe --tags --exact-match 2>$null)
}
if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw "必须用 -Tag 指定版本标签（例如 -Tag v0.0.10），或在已打好的 tag 上运行"
}
$Tag = $Tag.Trim()
$version = $Tag -replace '^[vV]', ''
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号必须是 X.Y.Z 形式：$Tag"
}

$zipName = "PackingProof-QQBot-$version-win-x64.zip"
$ppextName = "packingproof.qqbot-$version.ppext"
$zipPath = Join-Path $repoRoot "Release\$zipName"
$ppextPath = Join-Path $repoRoot "Release\$ppextName"

if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or -not (Test-Path -LiteralPath $ppextPath -PathType Leaf)) {
    if ($SkipBuild) {
        throw "缺少产物：$zipName / $ppextName（去掉 -SkipBuild 让脚本自己打）"
    }
    Write-Host "==> 产物不存在，先执行 Tools\Build-Ppext.ps1 -Version $version"
    & (Join-Path $PSScriptRoot "Build-Ppext.ps1") -Version $version
    if ($LASTEXITCODE -ne 0) { throw "打包失败，退出码 $LASTEXITCODE" }
}

Write-Host "==> 校验产物"
foreach ($required in @("PackingProof.QQBot.exe", "使用说明.pdf")) {
    if (-not (Test-ZipHasEntry -ZipPath $zipPath -Name $required)) {
        throw "独立包缺少 $required：$zipName"
    }
}
foreach ($required in @("manifest.json", "README.md", "payload/PackingProof.QQBot.exe", "payload/使用说明.pdf")) {
    if (-not (Test-ZipHasEntry -ZipPath $ppextPath -Name $required)) {
        throw "市场插件缺少 $required：$ppextName"
    }
}
if ((Test-ZipHasEntry -ZipPath $ppextPath -Name "payload/coreclr.dll") -or (Test-ZipHasEntry -ZipPath $ppextPath -Name "payload/hostfxr.dll")) {
    throw "市场插件不该自带运行时（应由桌面端提供）：$ppextName"
}

# 有市场仓库时，用市场自己的校验器再验一遍（同一套 schema 与包校验）。
$marketRoot = Join-Path (Split-Path -Parent $repoRoot) "PackingProof-Extensions"
if (Test-Path -LiteralPath (Join-Path $marketRoot "tools\lib\package-validator.mjs") -PathType Leaf) {
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) {
        $verifier = Join-Path $env:TEMP ("ppext-repo-verify-" + [Guid]::NewGuid().ToString("N") + ".mjs")
        $verifierSource = @"
import { createSchemaValidator } from "file:///$($marketRoot.Replace('\','/'))/tools/lib/schemas.mjs";
import { validatePackage } from "file:///$($marketRoot.Replace('\','/'))/tools/lib/package-validator.mjs";
const validator = await createSchemaValidator("$($marketRoot.Replace('\','/'))");
const result = await validatePackage(process.argv[2], validator);
console.log(JSON.stringify({ size: result.packageSize, warnings: result.warnings }));
"@
        [System.IO.File]::WriteAllText($verifier, $verifierSource, [System.Text.UTF8Encoding]::new($false))
        Write-Host "==> 用扩展市场的校验器复核市场插件"
        $marketResult = & $node.Source $verifier $ppextPath 2>&1
        if ($LASTEXITCODE -ne 0) { throw "市场校验未通过：`n$($marketResult -join [Environment]::NewLine)" }
        Write-Host ("  " + ($marketResult -join " "))
    }
}
else {
    Write-Host "提示：未找到 $marketRoot，跳过市场校验器复核"
}

$ppextInfo = Get-Item -LiteralPath $ppextPath
$ppextHash = (Get-FileHash -LiteralPath $ppextPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipInfo = Get-Item -LiteralPath $zipPath
$commit = (& git rev-parse HEAD).Trim()

Write-Host ""
Write-Host "独立包：$zipName（$([math]::Round($zipInfo.Length / 1MB, 2)) MB）"
Write-Host "市场插件：$ppextName（$([math]::Round($ppextInfo.Length / 1MB, 2)) MB，sha256=$ppextHash）"

$manifestJson = $null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($ppextPath)
try {
    $entry = $archive.Entries | Where-Object { $_.FullName -eq "manifest.json" } | Select-Object -First 1
    $reader = New-Object System.IO.StreamReader($entry.Open())
    try { $manifestJson = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
}
finally { $archive.Dispose() }

$downloadBase = @{
    github = "https://github.com/$repository/releases/download/$Tag/$ppextName"
    gitee  = "https://gitee.com/$repository/releases/download/$Tag/$ppextName"
}
$marketEntry = [ordered]@{
    '$schema'     = "../../../schemas/version.v1.schema.json"
    access        = $manifestJson.access
    compatibility = $manifestJson.compatibility
    downloads     = [ordered]@{
        gitee  = [ordered]@{ provider = "gitee"; url = $downloadBase.gitee }
        github = [ordered]@{ provider = "github"; url = $downloadBase.github }
    }
    extensionId   = $manifestJson.id
    publishedAt   = (Get-Date -Format "yyyy-MM-ddTHH:mm:sszzz")
    schemaVersion = 1
    sha256        = $ppextHash
    size          = $ppextInfo.Length
    source        = [ordered]@{ commit = $commit; tag = $Tag }
    version       = $version
}
Write-Host ""
Write-Host "==> 扩展市场条目片段（填进 $marketRoot\extensions\packingproof.qqbot\versions\$version.json）"
Write-Host ($marketEntry | ConvertTo-Json -Depth 8)

if ($ValidateOnly) {
    Write-Host ""
    Write-Host "校验模式：未上传任何文件"
    return
}

if (-not [string]::IsNullOrWhiteSpace($Title)) {
    $releaseTitle = if ($Title -like "v*") { $Title } else { "v$version $Title" }
}
else {
    $releaseTitle = "v$version"
    Write-Host "提示：未传 -Title，Release 标题只用版本号"
}

$notesText = ""
if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
    if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw "找不到发布笔记：$NotesFile" }
    $notesText = Get-Content -Raw -Encoding UTF8 -LiteralPath $NotesFile
}
else {
    $notesText = "详见仓库 README 与随包《使用说明.pdf》。"
    Write-Host "提示：未传 -NotesFile，发布正文用的是占位说明"
}

$assets = @($zipPath, $ppextPath)

if ($Target -eq "both" -or $Target -eq "github") {
    Assert-Command -Name "gh" -Hint "安装 GitHub CLI 并 gh auth login"
    Write-Host "==> GitHub Release $Tag"
    & gh release view $Tag --repo $repository *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  发布不存在，创建 Release"
        $notesFileTemp = Join-Path $env:TEMP ("qqbot-notes-" + [Guid]::NewGuid().ToString("N") + ".md")
        [System.IO.File]::WriteAllText($notesFileTemp, $notesText, [System.Text.UTF8Encoding]::new($false))
        & gh release create $Tag --repo $repository --title $releaseTitle --notes-file $notesFileTemp --verify-tag
        if ($LASTEXITCODE -ne 0) { throw "gh release create 失败，退出码 $LASTEXITCODE" }
    }
    else {
        & gh release edit $Tag --repo $repository --title $releaseTitle --notes $notesText *> $null
        if ($LASTEXITCODE -ne 0) { throw "gh release edit 失败，退出码 $LASTEXITCODE" }
    }
    & gh release upload $Tag @assets --repo $repository --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload 失败，退出码 $LASTEXITCODE" }
    Write-Host ("  GitHub 已上传：" + (($assets | ForEach-Object { Split-Path -Leaf $_ }) -join ", "))
}

if ($Target -eq "both" -or $Target -eq "gitee") {
    Assert-Command -Name "gitee" -Hint "安装 gitee CLI 并配置 GITEE_TOKEN"
    $tokenSource = Import-GiteeToken -RepositoryRoot $repoRoot
    if ([string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) {
        Write-Host "  未找到 GITEE_TOKEN（环境变量 / .env / 本机 apikey.txt），改用 gitee CLI 自己的登录态"
    }
    else {
        Write-Host "  Gitee 令牌来源：$tokenSource"
    }

    Write-Host "==> Gitee Release $Tag"
    & gitee release view $Tag --repo $repository *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  发布不存在，创建 Release"
        & gitee release create --repo $repository --tag $Tag --name $releaseTitle --notes $notesText --target main
        if ($LASTEXITCODE -ne 0) { throw "gitee release create 失败，退出码 $LASTEXITCODE" }
    }
    else {
        & gitee release edit --repo $repository --name $releaseTitle --notes $notesText $Tag *> $null
        if ($LASTEXITCODE -ne 0) { throw "gitee release edit 失败，退出码 $LASTEXITCODE" }
    }

    if ($ReplaceAssets -and -not [string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) {
        $releaseId = Get-GiteeReleaseId -Repo $repository -ReleaseTag $Tag
        foreach ($asset in $assets) {
            Remove-GiteeReleaseAttachmentByName -Repo $repository -ReleaseId $releaseId -FileName (Split-Path -Leaf $asset) | Out-Null
        }
    }

    & gitee release upload --repo $repository $Tag @assets
    if ($LASTEXITCODE -ne 0) {
        throw "gitee release upload 失败，退出码 $LASTEXITCODE（重复上传会被拒，加 -ReplaceAssets 先删同名旧附件）"
    }
    Write-Host ("  Gitee 已上传：" + (($assets | ForEach-Object { Split-Path -Leaf $_ }) -join ", "))
}

Write-Host ""
Write-Host "发布完成。别忘了把上面的市场条目片段提交到 PackingProof-Extensions。"
