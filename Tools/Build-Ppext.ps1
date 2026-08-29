[CmdletBinding()]
param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repositoryRoot "src\PackingProof.QQBot\PackingProof.QQBot.csproj"
$releaseRoot = Join-Path $repositoryRoot "Release"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = [System.IO.File]::ReadAllText($projectPath, [System.Text.Encoding]::UTF8)
    $Version = [string]$project.Project.PropertyGroup.Version
}
$Version = $Version.Trim()
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "版本必须是稳定 SemVer：$Version"
}

[System.IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
$releaseFullPath = [System.IO.Path]::GetFullPath($releaseRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$workingDirectory = Join-Path $releaseRoot (".ppext-build-" + [Guid]::NewGuid().ToString("N"))
$payloadDirectory = Join-Path $workingDirectory "payload"
$zipPath = Join-Path $releaseRoot "PackingProof-QQBot-$Version-win-x64.zip"
$ppextPath = Join-Path $releaseRoot "packingproof.qqbot-$Version.ppext"

foreach ($outputPath in @($zipPath, $ppextPath)) {
    if (Test-Path -LiteralPath $outputPath) {
        throw "发布文件已经存在，请先确认并移走旧文件：$outputPath"
    }
}

function Assert-UnderReleaseRoot([string]$Path) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($releaseFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "路径超出 Release 目录：$resolved"
    }
}

try {
    [System.IO.Directory]::CreateDirectory($payloadDirectory) | Out-Null
    & dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:Version=$Version -o $payloadDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出码：$LASTEXITCODE"
    }

    $executablePath = Join-Path $payloadDirectory "PackingProof.QQBot.exe"
    if (-not (Test-Path -LiteralPath $executablePath)) {
        throw "发布输出缺少 PackingProof.QQBot.exe"
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        format = "packingproof-extension"
        packageFormatVersion = 1
        id = "packingproof.qqbot"
        version = $Version
        type = "external-adapter"
        installation = [ordered]@{
            mode = "manual-external"
            suggestedPath = "payload/PackingProof.QQBot.exe"
        }
        compatibility = [ordered]@{
            minPackingProofVersion = "0.0.63"
            platforms = [ordered]@{
                windows = @("x64")
            }
        }
        access = [ordered]@{
            packingProofPermissions = @(
                "recordings.search",
                "recordings.download",
                "recordings.delivery"
            )
            packingProofCapabilities = @()
            systemAccess = @(
                [ordered]@{
                    id = "network"
                    reason = "连接 QQ 官方接口与用户配置的 PackingProof 主机"
                },
                [ordered]@{
                    id = "filesystem.read"
                    reason = "读取 QQBot 本机配置、加密凭据和运行状态"
                },
                [ordered]@{
                    id = "filesystem.write"
                    reason = "保存本机配置、加密凭据、日志和待发送的临时录像"
                },
                [ordered]@{
                    id = "other"
                    reason = "用户启用开机自动启动时写入当前 Windows 用户的启动项"
                }
            )
        }
    }

    $manifestJson = $manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText(
        (Join-Path $workingDirectory "manifest.json"),
        $manifestJson + "`n",
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::Copy(
        (Join-Path $repositoryRoot "README.md"),
        (Join-Path $workingDirectory "README.md"),
        $false)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadDirectory,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $workingDirectory,
        $ppextPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    Write-Host "已生成：$zipPath"
    Write-Host "已生成：$ppextPath"
}
finally {
    if (Test-Path -LiteralPath $workingDirectory) {
        Assert-UnderReleaseRoot $workingDirectory
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force
    }
}
