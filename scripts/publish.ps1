param(
    [string]$Version
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$buildProps = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "Directory.Build.props")
    $versionNode = $buildProps.SelectSingleNode("/Project/PropertyGroup/Version")
    $Version = $versionNode.InnerText
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "无法从 Directory.Build.props 读取版本号。"
}

$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$packageName = "TextPicker-$Version-win-x64"
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot $packageName))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "$packageName.zip"))
$checksumPath = "$archivePath.sha256"

if (!$publishDirectory.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    !$archivePath.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    !$checksumPath.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "发布路径必须位于 artifacts 目录内。"
}

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}

dotnet publish (Join-Path $repoRoot "src\TextPicker.App\TextPicker.App.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码 $LASTEXITCODE。"
}

Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $publishDirectory
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal

$archive = Get-Item -LiteralPath $archivePath
$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
Set-Content -LiteralPath $checksumPath -Value "$($hash.Hash)  $($archive.Name)" -NoNewline
Write-Output "发布包：$($archive.FullName)"
Write-Output "大小：$($archive.Length) bytes"
Write-Output "SHA256：$($hash.Hash)"
Write-Output "校验文件：$checksumPath"
