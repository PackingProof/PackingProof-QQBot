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
# 独立包的自包含发布目录必须放在 ppext 暂存目录之外，否则会被一起打进 .ppext。
$standaloneDirectory = Join-Path $releaseRoot (".standalone-build-" + [Guid]::NewGuid().ToString("N"))
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
    # 市场版（.ppext）由 PackingProof 桌面端启动，桌面端把自己的共享运行时通过 DOTNET_ROOT 交给它用，
    # 所以这里按框架依赖发布：几十 KB，不再自带整套 .NET。
    & dotnet publish $projectPath -c Release -r win-x64 --self-contained false -p:Version=$Version -o $payloadDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出码：$LASTEXITCODE"
    }

    $executablePath = Join-Path $payloadDirectory "PackingProof.QQBot.exe"
    if (-not (Test-Path -LiteralPath $executablePath)) {
        throw "发布输出缺少 PackingProof.QQBot.exe"
    }
    foreach ($runtimeOnlyFile in @("coreclr.dll", "hostfxr.dll", "System.Private.CoreLib.dll", "PresentationFramework.dll")) {
        if (Test-Path -LiteralPath (Join-Path $payloadDirectory $runtimeOnlyFile)) {
            throw "市场版必须复用桌面端运行时，payload 里不该出现 $runtimeOnlyFile"
        }
    }
    $payloadBytes = (Get-ChildItem -LiteralPath $payloadDirectory -File -Recurse | Measure-Object Length -Sum).Sum
    if ($payloadBytes -gt 8MB) {
        throw ("市场版 payload 体积异常：{0:N1} MB，应当只有几十 KB 量级" -f ($payloadBytes / 1MB))
    }

    # 跨机使用的独立包仍带自己的运行时（那种场景没有桌面端可复用），保持“免装 .NET”的承诺。
    [System.IO.Directory]::CreateDirectory($standaloneDirectory) | Out-Null
    & dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:Version=$Version -o $standaloneDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "独立包 dotnet publish 失败，退出码：$LASTEXITCODE"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $standaloneDirectory "PackingProof.QQBot.exe"))) {
        throw "独立包发布输出缺少 PackingProof.QQBot.exe"
    }

    # 发布包里的《使用说明》是 PDF（用户双击即可阅读），每次发布都由 docs\新手指南.md 现场转换，
    # 避免发布包里的教程和仓库里的新手指南脱节。
    $guidePdfPath = Join-Path $payloadDirectory "使用说明.pdf"
    & (Join-Path $PSScriptRoot "Build-UserGuidePdf.ps1") -OutputPath $guidePdfPath
    if (-not (Test-Path -LiteralPath $guidePdfPath -PathType Leaf)) {
        throw "发布输出缺少《使用说明.pdf》"
    }
    $guideHeader = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($guidePdfPath), 0, 5)
    if ($guideHeader -ne "%PDF-") {
        throw "生成的《使用说明.pdf》不是有效 PDF"
    }
    Copy-Item -LiteralPath $guidePdfPath -Destination (Join-Path $standaloneDirectory "使用说明.pdf") -Force

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
        launcher = [ordered]@{
            path = "payload/PackingProof.QQBot.exe"
        }
        compatibility = [ordered]@{
            # 机器人已迁到 .NET 10，必须用桌面端共享运行时里的 10.x 框架（0.0.82 起的布局才是 .NET 10，
            # 0.0.63~0.0.81 是 .NET 8，装上去起不来）。
            # 另外机器人现在的配置目录走扩展 API 规范：桌面端注入 PACKINGPROOF_EXTENSION_DATA_DIR、
            # 卸载时清理 extension-data，这两件事从 0.0.85 才有；所以门槛定 0.0.85。
            minPackingProofVersion = "0.0.85"
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
        $standaloneDirectory,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $workingDirectory,
        $ppextPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    foreach ($artifactPath in @($zipPath, $ppextPath)) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($artifactPath)
        try {
            $guideEntry = $archive.Entries | Where-Object { $_.Name -eq "使用说明.pdf" } | Select-Object -First 1
            if (-not $guideEntry -or $guideEntry.Length -eq 0) {
                throw "发布包缺少《使用说明.pdf》：$artifactPath"
            }
            $guideStream = $guideEntry.Open()
            try {
                $headerBytes = New-Object byte[] 5
                $headerRead = $guideStream.Read($headerBytes, 0, 5)
                if ($headerRead -ne 5 -or [System.Text.Encoding]::ASCII.GetString($headerBytes) -ne "%PDF-") {
                    throw "发布包里的《使用说明.pdf》不是有效 PDF：$artifactPath"
                }
            }
            finally { $guideStream.Dispose() }
        }
        finally {
            $archive.Dispose()
        }
    }

    Write-Host "已生成：$zipPath"
    Write-Host "已生成：$ppextPath"
}
finally {
    if (Test-Path -LiteralPath $workingDirectory) {
        Assert-UnderReleaseRoot $workingDirectory
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $standaloneDirectory) {
        Assert-UnderReleaseRoot $standaloneDirectory
        Remove-Item -LiteralPath $standaloneDirectory -Recurse -Force
    }
}
