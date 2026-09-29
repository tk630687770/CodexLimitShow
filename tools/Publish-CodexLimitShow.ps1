$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'CodexLimitShow.csproj'
$release = Join-Path $root 'release'
$candidate = Join-Path $release 'CodexLimitShow.exe'
$xml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$xml.Project.PropertyGroup.Version
$parsedVersion = $null
if (-not [version]::TryParse($version, [ref]$parsedVersion)) { throw '项目版本号无效。' }
if (Test-Path -LiteralPath $candidate) {
    $previous = [Diagnostics.FileVersionInfo]::GetVersionInfo($candidate).ProductVersion
    $previousVersion = $null
    if (-not [version]::TryParse($previous, [ref]$previousVersion) -or $parsedVersion -le $previousVersion) {
        throw "发布版本必须高于当前候选程序：$previous"
    }
}

& dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release 构建失败。' }
& dotnet run --project $project --no-build -c Release -- --self-test
if ($LASTEXITCODE -ne 0) { throw '自测未通过。' }

New-Item -ItemType Directory -Path $release -Force | Out-Null
$staging = Join-Path $release ".staging-$([guid]::NewGuid().ToString('N'))"
$newFile = Join-Path $release ".CodexLimitShow-$([guid]::NewGuid().ToString('N')).exe"
try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $staging
    if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
    $exe = Join-Path $staging 'CodexLimitShow.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '发布程序缺失。' }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($info.ProductName -ne 'CodexLimitShow' -or $info.ProductVersion -ne $version -or
        -not $info.FileVersion.StartsWith("$version.")) { throw "程序版本与项目版本 $version 不一致。" }
    Copy-Item -LiteralPath $exe -Destination $newFile
    if ((Get-FileHash -LiteralPath $newFile -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash) { throw '发布程序复制校验失败。' }
    [IO.File]::Move($newFile, $candidate, (Test-Path -LiteralPath $candidate))
    Write-Output "待上传 GitHub Release 的单文件程序：$candidate"
    Write-Output "版本：$version"
    Write-Output "SHA-256：$((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash)"
}
finally {
    if ($staging.StartsWith($release.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $staging).StartsWith('.staging-') -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    if ($newFile.StartsWith($release.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $newFile).StartsWith('.CodexLimitShow-') -and (Test-Path -LiteralPath $newFile)) {
        Remove-Item -LiteralPath $newFile -Force
    }
}
