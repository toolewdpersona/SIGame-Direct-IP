$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $process = Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    exit $process.ExitCode
}
$program = Join-Path (Split-Path -Parent $PSScriptRoot) 'DirectHost\SIGame.DirectHost.exe'
if (-not (Test-Path -LiteralPath $program -PathType Leaf)) { throw 'DirectHost executable not found. Extract the complete add-on first.' }
$program = (Resolve-Path -LiteralPath $program).Path
$hashAlgorithm = [Security.Cryptography.SHA256]::Create()
try {
    $pathHash = [BitConverter]::ToString($hashAlgorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($program.ToLowerInvariant()))).Replace('-', '').Substring(0, 12)
} finally { $hashAlgorithm.Dispose() }
$ruleName = 'SIGame-DirectIP-TCP-51234-' + $pathHash
# Recognize earlier installations without duplicating or replacing their rules.
$legacyRuleName = 'SIGame-DirectIP-1.0.3-TCP-51234-' + $pathHash
if (-not (Get-NetFirewallRule -Name @($ruleName, $legacyRuleName) -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name $ruleName -DisplayName ('SIGame Direct IP (TCP 51234) ' + $pathHash) -Description $program -Direction Inbound -Action Allow -Protocol TCP -LocalPort 51234 -Program $program -Profile Private,Public | Out-Null
}
Write-Host 'TCP 51234 rule is available for this add-on. Existing rules were preserved.'
