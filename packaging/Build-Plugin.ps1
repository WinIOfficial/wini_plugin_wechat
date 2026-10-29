param(
    [string]$Version = '0.1.0',
    [string]$ContractsProject,
    [string]$ContractsFeed,
    [string]$ContractsVersion = '0.1.1'
)
$ErrorActionPreference = 'Stop'
function Assert-SemVer([string]$Value, [string]$Field = 'version') {
    $numeric = '(?:0|[1-9][0-9]*)'
    $identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
    if ($Value -notmatch "\A$numeric\.$numeric\.$numeric(?:-$identifier(?:\.$identifier)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z") { throw "$Field must be strict SemVer 2.0." }
}
Assert-SemVer $Version
Assert-SemVer $ContractsVersion 'ContractsVersion'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json
Assert-SemVer $manifest.minHostVersion 'manifest.minHostVersion'
Assert-SemVer $manifest.minimumHostVersion 'manifest.minimumHostVersion'
if ($manifest.minHostVersion -cne $manifest.minimumHostVersion) { throw 'Manifest host version fields must match.' }
$manifest.version = $Version
$buildArguments = @('-c', 'Release', "-p:Version=$Version", "-p:ContractsVersion=$ContractsVersion", '--disable-build-servers')
if ($ContractsProject) { $buildArguments += "-p:ContractsProject=$((Resolve-Path -LiteralPath $ContractsProject).Path)" }
if ($ContractsFeed) { $buildArguments += "-p:ContractsFeed=$((Resolve-Path -LiteralPath $ContractsFeed).Path)" }
$stage = Join-Path $root ('artifacts/package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
dotnet build (Join-Path $root 'src/Wini.Plugin.WeChat/Wini.Plugin.WeChat.csproj') @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$binary = Join-Path $root 'artifacts/bin/Wini.Plugin.WeChat/Release/net10.0/Wini.Plugin.WeChat.dll'
Copy-Item -LiteralPath $binary -Destination $stage
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage
$zip = Join-Path $root "artifacts/wini_plugin_wechat-$Version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "$zip.sha256" -Encoding utf8
[ordered]@{
    id = $manifest.id; name = $manifest.name; version = $Version
    downloadUrl = "https://github.com/WinIOfficial/wini_plugin_wechat/releases/download/v$Version/$([IO.Path]::GetFileName($zip))"
    sha256 = $hash; minHostVersion = $manifest.minHostVersion; contractVersion = $manifest.contractVersion
    permissions = $manifest.permissions; description = $manifest.description
    repositoryUrl = $manifest.repositoryUrl; developerId = $manifest.developerId
} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $root "artifacts/catalog-$Version.json") -Encoding utf8
Write-Output $zip
