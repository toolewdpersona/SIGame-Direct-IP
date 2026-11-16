#requires -Version 5.1
param(
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory = 'publish-exe',
    [string]$WorkDirectory = '.build',
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot

function Resolve-BuildPath([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $sourceRoot $Path }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Test-InDirectory([string]$Path, [string]$Directory) {
    return $Path.Equals($Directory, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Directory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Run-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

$workPath = Resolve-BuildPath $WorkDirectory
$outputPath = Resolve-BuildPath $OutputDirectory
if (Test-Path -LiteralPath $workPath) { throw 'Choose a new WorkDirectory; existing sources are not replaced.' }
if (-not $PrepareOnly -and (Test-Path -LiteralPath $outputPath)) { throw 'Choose a new OutputDirectory; existing files are not replaced.' }
if ((Test-InDirectory $workPath $outputPath) -or (Test-InDirectory $outputPath $workPath)) {
    throw 'WorkDirectory and OutputDirectory must be separate directories.'
}
foreach ($name in @('DirectHost', 'DirectIP-Docs', 'DirectIP-Tools', 'patches')) {
    $protectedPath = Join-Path $sourceRoot $name
    if ((Test-InDirectory $workPath $protectedPath) -or (Test-InDirectory $outputPath $protectedPath)) {
        throw 'Build directories must be outside the source and documentation directories.'
    }
}
$commands = @('git')
if (-not $PrepareOnly) { $commands += @('npm.cmd', 'cargo', $Dotnet) }
foreach ($command in $commands) { Get-Command $command -ErrorAction Stop | Out-Null }

$patchRoot = Join-Path $sourceRoot 'patches'
$manifest = Get-Content -LiteralPath (Join-Path $patchRoot 'upstream.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or ($manifest.projects.name -join ',') -ne 'SI,SIOnline') {
    throw 'Unsupported upstream manifest.'
}
foreach ($project in $manifest.projects) {
    if ($project.commit -notmatch '^[a-f0-9]{40}$' -or $project.patchedTree -notmatch '^[a-f0-9]{40}$' -or
        $project.patch -ne ($project.name + '.patch')) { throw 'Invalid pinned source or patch name.' }
    $patchPath = Join-Path $patchRoot $project.patch
    if ((Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash -ne $project.sha256) {
        throw "Patch checksum mismatch: $($project.name)."
    }
}

New-Item -ItemType Directory -Path $workPath | Out-Null
foreach ($project in $manifest.projects) {
    $projectPath = Join-Path $workPath $project.name
    $patchPath = Join-Path $patchRoot $project.patch
    Write-Host "Preparing $($project.name) at $($project.commit)..."
    Run-Checked 'git' @('init', '--quiet', $projectPath)
    Run-Checked 'git' @('-C', $projectPath, 'config', 'core.autocrlf', 'false')
    Run-Checked 'git' @('-C', $projectPath, 'config', 'core.longpaths', 'true')
    Run-Checked 'git' @('-C', $projectPath, 'remote', 'add', 'origin', $project.repository)
    Run-Checked 'git' @('-C', $projectPath, 'fetch', '--quiet', '--depth', '1', 'origin', $project.commit)
    # Materialize only the required engine projects; omit unused fonts and bundled Steam DLLs.
    Run-Checked 'git' @('-C', $projectPath, 'sparse-checkout', 'init', '--no-cone')
    $project.sparsePaths | & git -C $projectPath sparse-checkout set --no-cone --stdin
    if ($LASTEXITCODE -ne 0) { throw "Cannot configure source paths for $($project.name)." }
    Run-Checked 'git' @('-C', $projectPath, 'checkout', '--quiet', '--detach', $project.commit)
    $actualCommit = Run-Checked 'git' @('-C', $projectPath, 'rev-parse', 'HEAD')
    if ($actualCommit -ne $project.commit) { throw 'Upstream commit mismatch.' }
    Run-Checked 'git' @('-C', $projectPath, 'apply', '--check', '--index', $patchPath)
    Run-Checked 'git' @('-C', $projectPath, 'apply', '--index', '--whitespace=nowarn', $patchPath)
    $actualTree = Run-Checked 'git' @('-C', $projectPath, 'write-tree')
    if ($actualTree -ne $project.patchedTree) { throw "Patched source mismatch: $($project.name)." }
}
New-Item -ItemType Directory -Path (Join-Path $workPath 'DirectHost') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'DirectHost') -Exclude bin,obj -Force |
    Copy-Item -Destination (Join-Path $workPath 'DirectHost') -Recurse
Copy-Item -LiteralPath (Join-Path $sourceRoot 'mod-version.json') -Destination $workPath
if ($PrepareOnly) {
    Write-Host "Verified sources prepared in $workPath. Nothing was built, installed or uploaded."
    return
}

function Build-NativeClient([string]$BuildRoot) {
    # Cargo's encoded flags preserve spaces in Windows paths. Restore the caller's
    # environment afterwards; these flags affect compilation, not runtime paths.
    $previousFlags = $env:CARGO_ENCODED_RUSTFLAGS
    $flags = @()
    if ($previousFlags) { $flags += $previousFlags.Split([char]31) }
    elseif ($env:RUSTFLAGS) { $flags += $env:RUSTFLAGS -split '\s+' | Where-Object { $_ } }
    $mappings = @(
        @($env:USERPROFILE, '/_/user'),
        @($env:CARGO_HOME, '/_/cargo'),
        @($env:RUSTUP_HOME, '/_/rustup'),
        @($BuildRoot, '/_/src')
    )
    foreach ($mapping in $mappings) {
        if ($mapping[0]) {
            $path = [IO.Path]::GetFullPath($mapping[0]).TrimEnd('\', '/')
            foreach ($variant in @($path, $path.Replace('\', '/')) | Select-Object -Unique) {
                $flags += '--remap-path-prefix=' + $variant + '=' + $mapping[1]
            }
        }
    }
    $env:CARGO_ENCODED_RUSTFLAGS = $flags -join [char]31
    try {
        Run-Checked 'npm.cmd' @('run','tauri','build','--','--features','steam_client','--no-bundle')
    } finally { $env:CARGO_ENCODED_RUSTFLAGS = $previousFlags }
}

Push-Location (Join-Path $workPath 'SIOnline')
try {
    Run-Checked 'npm.cmd' @('ci')
    Run-Checked 'npm.cmd' @('run','build-steam')
    Run-Checked 'npm.cmd' @('run','build-direct-web')
} finally { Pop-Location }
Push-Location (Join-Path $workPath 'SIOnline/tauri')
try {
    Run-Checked 'npm.cmd' @('ci')
    Build-NativeClient $workPath
} finally { Pop-Location }
New-Item -ItemType Directory -Path $outputPath | Out-Null
Run-Checked $Dotnet @('publish',(Join-Path $workPath 'DirectHost/SIGame.DirectHost.csproj'),'-c','Release','-r','win-x64','--self-contained','true',('-p:PathMap=' + $workPath + '=/_/src'),'-o',(Join-Path $outputPath 'DirectHost'))
Copy-Item -LiteralPath (Join-Path $workPath 'SIOnline/tauri/src-tauri/target/release/SIGame.exe') -Destination $outputPath
foreach ($name in @('README-RU.txt')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $outputPath
}
foreach ($name in @('DirectIP-Docs','DirectIP-Tools')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $outputPath -Recurse
}
Write-Host "Built SIGame.exe and DirectHost in $outputPath. Nothing was installed or uploaded."
