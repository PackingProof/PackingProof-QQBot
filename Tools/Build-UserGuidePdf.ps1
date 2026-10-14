[CmdletBinding()]
param(
    [string]$OutputPath = "",
    [string]$SourcePath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# 发布包里的《使用说明》是 PDF（用户双击即可阅读），源文件仍然是 docs\新手指南.md。
# 这里把 Markdown 转成自带样式的 HTML，再交给 Edge/Chrome 的无头模式打印成 PDF：
# 不引入额外依赖，也不需要在打包机上安装任何转换工具。

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($SourcePath)) { $SourcePath = Join-Path $repositoryRoot "docs\新手指南.md" }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $repositoryRoot "Release\使用说明.pdf" }

$sourceFullPath = [System.IO.Path]::GetFullPath($SourcePath)
if (-not (Test-Path -LiteralPath $sourceFullPath -PathType Leaf)) {
    throw "找不到教程源文件：$sourceFullPath"
}
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($outputFullPath)) | Out-Null

function Find-BrowserExecutable {
    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $candidates.Add((Join-Path $root "Microsoft\Edge\Application\msedge.exe"))
        $candidates.Add((Join-Path $root "Google\Chrome\Application\chrome.exe"))
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "未找到 Edge 或 Chrome，无法把《使用说明》导出为 PDF"
}

function ConvertTo-HtmlText {
    param([string]$Text)
    return $Text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
}

function ConvertTo-InlineHtml {
    param([string]$Text)

    $html = ConvertTo-HtmlText $Text
    $html = [regex]::Replace($html, '\*\*(.+?)\*\*', '<strong>$1</strong>')
    $html = [regex]::Replace($html, '`(.+?)`', '<code>$1</code>')
    $html = [regex]::Replace($html, '\[([^\]]+)\]\((https?://[^\)]+)\)', '<a href="$2">$1</a>')
    $html = [regex]::Replace($html, '&lt;(https?://[^&]+?)&gt;', '<a href="$1">$1</a>')
    return $html
}

function Convert-MarkdownToHtml {
    param([string]$Markdown)

    $lines = ($Markdown -replace "`r`n", "`n") -split "`n"
    $blocks = [System.Collections.Generic.List[object]]::new()
    $current = $null
    $inCode = $false

    foreach ($line in $lines) {
        if ($line -match '^\s*```') {
            if ($inCode) { $inCode = $false; $current = $null; continue }
            $current = @{ Kind = "code"; Lines = [System.Collections.Generic.List[string]]::new() }
            $blocks.Add($current)
            $inCode = $true
            continue
        }
        if ($inCode) { $current.Lines.Add($line); continue }

        if ([string]::IsNullOrWhiteSpace($line)) { $current = $null; continue }

        $kind = ""
        $text = $line
        if ($line -match '^(#{1,6})\s+(.*)$') { $kind = "heading:$($Matches[1].Length)"; $text = $Matches[2] }
        elseif ($line -match '^\s*[-*]\s+(.*)$') { $kind = "ul"; $text = $Matches[1] }
        elseif ($line -match '^\s*\d+\.\s+(.*)$') { $kind = "ol"; $text = $Matches[1] }
        elseif ($line -match '^\s*\|') { $kind = "table" }
        else { $kind = "paragraph" }

        $sameKind = $null -ne $current -and $current.Kind -eq $kind
        if (-not $sameKind) {
            $current = @{ Kind = $kind; Lines = [System.Collections.Generic.List[string]]::new() }
            $blocks.Add($current)
        }
        $current.Lines.Add($text)
    }

    $body = [System.Text.StringBuilder]::new()
    foreach ($block in $blocks) {
        switch -Regex ($block.Kind) {
            '^heading:(\d+)$' {
                $level = [int]$Matches[1]
                [void]$body.AppendLine("<h$level>" + (ConvertTo-InlineHtml $block.Lines[0]) + "</h$level>")
            }
            '^paragraph$' {
                [void]$body.AppendLine("<p>" + (ConvertTo-InlineHtml ($block.Lines -join " ")) + "</p>")
            }
            '^ul$' {
                [void]$body.AppendLine("<ul>")
                foreach ($item in $block.Lines) { [void]$body.AppendLine("<li>" + (ConvertTo-InlineHtml $item) + "</li>") }
                [void]$body.AppendLine("</ul>")
            }
            '^ol$' {
                [void]$body.AppendLine("<ol>")
                foreach ($item in $block.Lines) { [void]$body.AppendLine("<li>" + (ConvertTo-InlineHtml $item) + "</li>") }
                [void]$body.AppendLine("</ol>")
            }
            '^code$' {
                [void]$body.AppendLine("<pre><code>" + (ConvertTo-HtmlText ($block.Lines -join "`n")) + "</code></pre>")
            }
            '^table$' {
                $rows = @($block.Lines | Where-Object { $_ -notmatch '^\s*\|[\s\-\|:]+\|\s*$' })
                [void]$body.AppendLine("<table>")
                for ($index = 0; $index -lt $rows.Count; $index++) {
                    $cells = @($rows[$index].Trim().Trim('|') -split '\|' | ForEach-Object { ConvertTo-InlineHtml $_.Trim() })
                    $tag = "td"
                    if ($index -eq 0) { $tag = "th" }
                    [void]$body.AppendLine("<tr>" + (($cells | ForEach-Object { "<$tag>$_</$tag>" }) -join "") + "</tr>")
                }
                [void]$body.AppendLine("</table>")
            }
        }
    }

    return @{ Html = $body.ToString(); Blocks = $blocks }
}

$markdown = [System.IO.File]::ReadAllText($sourceFullPath, [System.Text.Encoding]::UTF8)
$converted = Convert-MarkdownToHtml -Markdown $markdown
$bodyHtml = $converted.Html

# 转换自检：标题和表格不能悄悄丢，否则宁可让打包失败。
$expectedHeadings = @($markdown -split "`n" | Where-Object { $_ -match '^##\s' }).Count
$actualHeadings = ([regex]::Matches($bodyHtml, '<h2>')).Count
if ($expectedHeadings -ne $actualHeadings) {
    throw "教程转换丢失内容：$expectedHeadings 个二级标题只转换出 $actualHeadings 个"
}
$expectedTables = ([regex]::Matches($markdown, '(?m)^\s*\|')).Count
if ($expectedTables -gt 0 -and ([regex]::Matches($bodyHtml, '<table>')).Count -eq 0) {
    throw "教程里的表格没有被转换"
}
if ($bodyHtml -match '\*\*' -or $bodyHtml -match '(?m)^\|') {
    throw "教程转换残留未处理的 Markdown 标记"
}

$html = @"
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>PackingProof QQBot 使用说明</title>
<style>
  @page { size: A4; margin: 16mm 14mm; }
  body { font-family: "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", sans-serif; font-size: 10.5pt; line-height: 1.75; color: #111827; }
  h1 { font-size: 18pt; margin: 0 0 14px 0; padding-bottom: 8px; border-bottom: 2px solid #374151; }
  h2 { font-size: 13.5pt; margin: 20px 0 8px 0; padding-left: 8px; border-left: 4px solid #2563eb; page-break-after: avoid; }
  p { margin: 7px 0; }
  code { font-family: Consolas, "Courier New", monospace; font-size: 9.5pt; background: #f3f4f6; padding: 1px 4px; border-radius: 3px; }
  pre { background: #f8fafc; border: 1px solid #e5e7eb; border-radius: 4px; padding: 8px 10px; overflow: hidden; }
  pre code { background: none; padding: 0; }
  table { border-collapse: collapse; width: 100%; margin: 8px 0; }
  th, td { border: 1px solid #d1d5db; padding: 5px 8px; text-align: left; vertical-align: top; font-size: 10pt; }
  th { background: #f3f4f6; }
  ul, ol { margin: 6px 0; padding-left: 22px; }
  li { margin: 3px 0; }
  a { color: #2563eb; text-decoration: none; word-break: break-all; }
</style>
</head>
<body>
$bodyHtml
</body>
</html>
"@

$browserPath = Find-BrowserExecutable
$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("packingproof-userguide-" + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null

try {
    $htmlPath = Join-Path $temporaryDirectory "使用说明.html"
    [System.IO.File]::WriteAllText($htmlPath, $html, [System.Text.UTF8Encoding]::new($false))
    $profileDirectory = Join-Path $temporaryDirectory "browser-profile"
    $fileUri = [System.Uri]::new($htmlPath).AbsoluteUri

    $pdfReady = $false
    foreach ($headlessSwitch in @("--headless=new", "--headless")) {
        if (Test-Path -LiteralPath $outputFullPath) { Remove-Item -LiteralPath $outputFullPath -Force }

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $browserPath
        $startInfo.Arguments = "$headlessSwitch --disable-gpu --no-first-run --no-pdf-header-footer " +
            "--user-data-dir=`"$profileDirectory`" --print-to-pdf=`"$outputFullPath`" `"$fileUri`""
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardError = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.CreateNoWindow = $true

        $process = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) { continue }
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()

        # 无头浏览器会先把请求交棒给浏览器进程，PDF 是异步写出的，所以这里轮询等待。
        for ($waited = 0; $waited -lt 60; $waited++) {
            if (Test-Path -LiteralPath $outputFullPath -PathType Leaf) {
                try {
                    $head = [System.IO.File]::ReadAllBytes($outputFullPath)
                    if ($head.Length -gt 5 -and [System.Text.Encoding]::ASCII.GetString($head, 0, 5) -eq "%PDF-") {
                        $pdfReady = $true
                        break
                    }
                }
                catch {
                    Write-Verbose "PDF 还在写入：$($_.Exception.Message)"
                }
            }
            Start-Sleep -Milliseconds 500
        }
        if ($pdfReady) { break }
        Write-Verbose "$headlessSwitch 未生成 PDF：$standardError"
    }

    if (-not $pdfReady) {
        throw "无头浏览器没有生成 PDF：$outputFullPath"
    }

    $bytes = [System.IO.File]::ReadAllBytes($outputFullPath)
    if ($bytes.Length -lt 5 -or [System.Text.Encoding]::ASCII.GetString($bytes, 0, 5) -ne "%PDF-") {
        throw "生成的《使用说明.pdf》不是有效 PDF"
    }

    Write-Host "已生成：$outputFullPath（$([math]::Round($bytes.Length / 1KB)) KB）"
}
finally {
    # 无头浏览器退出后可能还短暂占着 profile 目录，清理失败不影响打包结果。
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            if (-not (Test-Path -LiteralPath $temporaryDirectory)) { break }
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
            break
        }
        catch {
            Write-Verbose "清理临时目录失败，稍后重试：$($_.Exception.Message)"
            Start-Sleep -Milliseconds 500
        }
    }
}
