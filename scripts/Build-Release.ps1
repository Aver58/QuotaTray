param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'CodexQuotaNative/CodexQuotaNative.csproj'
$version = ([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a numerical X.Y.Z version.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }
$dir = [IO.Directory]::CreateDirectory($OutputDirectory).FullName

$publish = Join-Path $dir "build-v$version"
dotnet publish $project -c Release -r win-x64 -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE) { throw 'Native publish failed.' }

$nativeName = "CodexQuotaTray-v$version-win-x64.zip"
$nativeZip = Join-Path $dir $nativeName
# Package only the executable so local configuration cannot enter the ZIP.
Compress-Archive -LiteralPath (Join-Path $publish 'CodexQuotaTray.exe') -DestinationPath $nativeZip -CompressionLevel Optimal -Force

# Existing lightweight clients validate this filename before they can install the native build.
$lightweightName = "CodexQuotaTray-v$version-win-x64-lightweight.zip"
Copy-Item -LiteralPath $nativeZip -Destination (Join-Path $dir $lightweightName) -Force
$size = (Get-Item -LiteralPath $nativeZip).Length
$sha256 = (Get-FileHash -LiteralPath $nativeZip).Hash.ToLowerInvariant()
$names = @{ 'latest.json' = $nativeName; 'latest-lightweight.json' = $lightweightName }
foreach ($manifestName in $names.Keys) {
    $manifest = [ordered]@{
        tag = "v$version"
        asset = $names[$manifestName]
        size = $size
        sha256 = $sha256
        runtime = 'win32'
    }
    [IO.File]::WriteAllText((Join-Path $dir $manifestName), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
