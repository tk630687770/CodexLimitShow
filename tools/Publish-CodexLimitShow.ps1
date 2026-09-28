param([switch]$InstallFormal)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'CodexLimitShow.csproj'
$readme = Join-Path $root 'README.md'
$versions = Join-Path $root 'release\versions'
$packages = Join-Path $root 'release\packages'
$formal = Join-Path $root '正式版'
$xml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$xml.Project.PropertyGroup.Version
$parsedVersion = $null
if (-not [version]::TryParse($version, [ref]$parsedVersion)) { throw '项目版本号无效。' }
$name = "CodexLimitShow-$version-win-x64"
$release = Join-Path $versions $name
$zip = Join-Path $packages "$name.zip"
if ((Test-Path -LiteralPath $release) -or (Test-Path -LiteralPath $zip)) { throw "该版本已发布，禁止覆盖：$name" }
if ($InstallFormal -and (Test-Path -LiteralPath $formal)) { throw '正式版已存在，请从正式版界面升级。' }

function Assert-ProjectPath([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "路径超出项目目录：$resolved"
    }
}

& dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release 构建失败。' }
& dotnet run --project $project --no-build -c Release -- --self-test
if ($LASTEXITCODE -ne 0) { throw '自测未通过。' }

New-Item -ItemType Directory -Path $versions, $packages -Force | Out-Null
$staging = Join-Path $versions ".staging-$([guid]::NewGuid().ToString('N'))"
Assert-ProjectPath $staging
Assert-ProjectPath $release
try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $staging
    if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
    $exe = Join-Path $staging 'CodexLimitShow.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '发布程序缺失。' }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($info.ProductVersion -ne $version -or -not $info.FileVersion.StartsWith("$version.")) {
        throw "程序版本与项目版本 $version 不一致。"
    }
    Copy-Item -LiteralPath $readme -Destination $staging
    [ordered]@{
        Version = $version
        ExecutableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'release.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $staging 'formal.marker') -Value $version -Encoding UTF8
    Move-Item -LiteralPath $staging -Destination $release
    Compress-Archive -LiteralPath $release -DestinationPath $zip -CompressionLevel Optimal
    if ($InstallFormal) {
        $formalStaging = Join-Path $root ".正式版-暂存-$([guid]::NewGuid().ToString('N'))"
        Assert-ProjectPath $formalStaging
        Assert-ProjectPath $formal
        Copy-Item -LiteralPath $release -Destination $formalStaging -Recurse
        Set-Content -LiteralPath (Join-Path $formalStaging 'formal.marker') -Value $version -Encoding UTF8
        Move-Item -LiteralPath $formalStaging -Destination $formal
    }
    Write-Output "版本目录：$release"
    Write-Output "发布包：$zip"
    if ($InstallFormal) { Write-Output "正式版入口：$(Join-Path $formal 'CodexLimitShow.exe')" }
    Write-Output "ZIP SHA-256：$((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)"
}
catch {
    if (Test-Path -LiteralPath $staging) {
        Assert-ProjectPath $staging
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    throw
}
