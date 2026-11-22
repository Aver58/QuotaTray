param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'CodexQuotaTray/CodexQuotaTray.csproj'
$version = ([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a numerical X.Y.Z version.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }
$dir = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
$publish = Join-Path $dir "build-v$version"
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE) { throw 'Publish failed.' }
$name = "CodexQuotaTray-v$version-win-x64.zip"
$zip = Join-Path $dir $name
# Package only this binary, even if the local output folder contains user files.
Compress-Archive -LiteralPath (Join-Path $publish 'CodexQuotaTray.exe') -DestinationPath $zip -CompressionLevel Optimal -Force
$manifest = [ordered]@{tag="v$version";asset=$name;size=(Get-Item -LiteralPath $zip).Length;sha256=(Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()}
[IO.File]::WriteAllText((Join-Path $dir 'latest.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
