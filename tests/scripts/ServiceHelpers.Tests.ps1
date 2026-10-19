<#
.SYNOPSIS
    Tests of the decisions in tools\ServiceHelpers.ps1 that need no Windows (CI runs them with pwsh on Linux):
    quoting for PowerShell (SV4 X-04) and what happens to a data folder someone else prepared (SV4 X-02).

.EXAMPLE
    pwsh -NoProfile -File tests/scripts/ServiceHelpers.Tests.ps1
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\tools\ServiceHelpers.ps1')

$script:failures = 0
$script:count = 0
function Check([string]$name, $actual, $expected) {
    $script:count++
    if ($actual -cne $expected) {
        Write-Host "FAIL $name`n  expected: $expected`n  actual:   $actual" -ForegroundColor Red
        $script:failures++
    }
}

# ---- ConvertTo-StreamZhLiteral: the value comes back exactly, nothing after it runs ----
$values = @(
    'D:\Media',
    "D:\John's Movies",
    "D:\John$([char]0x2019)s Movies",
    "D:\Media$([char]0x2019); Write-Output INJECTED; $([char]0x2019)",
    "D:\$([char]0x2018)a$([char]0x201A)b$([char]0x201B)c''d",
    'C:\Program Files\StreamZH',
    'D:\$env:TEMP `n "quoted"'
)
foreach ($value in $values) {
    $literal = ConvertTo-StreamZhLiteral $value
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput("& 'x.ps1' -Folder $literal, 'D:\Downloads'", [ref]$tokens, [ref]$errors)
    Check "no parse errors for $value" $errors.Count 0
    $command = $ast.EndBlock.Statements[0].PipelineElements[0]
    Check "one command for $value" $ast.EndBlock.Statements.Count 1
    $list = $command.CommandElements[2]
    Check "the folder value of $value" $list.Elements[0].Value $value
    Check "the second folder after $value" $list.Elements[1].Value 'D:\Downloads'
}

# ---- Get-StreamZhDataFolderAction ----
$me = 'S-1-5-21-1000000001-1000000002-1000000003-1001'
$other = 'S-1-5-21-1000000001-1000000002-1000000003-1002'
$service = 'S-1-5-80-1111111111-2222222222-3333333333-4444444444-555555555'
$trustedInstaller = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
$cases = @(
    # exists, link, owner, empty, existing install -> action
    @($false, $false, '', $false, $false, 'create'),
    @($true, $true, $me, $false, $true, 'refuse-link'),
    @($true, $true, 'S-1-5-32-544', $true, $false, 'refuse-link'),
    @($true, $false, 'S-1-5-32-544', $false, $false, 'protect'),
    @($true, $false, 'S-1-5-18', $false, $false, 'protect'),
    @($true, $false, $trustedInstaller, $false, $false, 'protect'),
    @($true, $false, $me, $false, $false, 'protect'),
    @($true, $false, $me.ToLowerInvariant(), $false, $false, 'protect'),
    @($true, $false, $service, $false, $false, 'protect'),
    @($true, $false, $other, $true, $false, 'recreate'),
    @($true, $false, $other, $true, $true, 'recreate'),
    @($true, $false, $other, $false, $true, 'take-ownership'),
    @($true, $false, $other, $false, $false, 'refuse-owner'),
    @($true, $false, '', $false, $false, 'refuse-owner'),
    @($true, $false, '', $true, $false, 'recreate')
)
foreach ($case in $cases) {
    $action = Get-StreamZhDataFolderAction $case[0] $case[1] $case[2] $case[3] @($me, $service) $case[4]
    Check "exists=$($case[0]) link=$($case[1]) owner=$($case[2]) empty=$($case[3]) existing=$($case[4])" $action $case[5]
}
Check 'no trusted accounts given' (Get-StreamZhDataFolderAction $true $false $me $false @() $false) 'refuse-owner'

# ---- Get-StreamZhDataFolderSddl: exactly these entries, protected ----
Check 'service and user' (Get-StreamZhDataFolderSddl @($service) @($me)) "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;$service)(A;OICI;0x1301bf;;;$me)"
Check 'tray only' (Get-StreamZhDataFolderSddl @() @($me)) "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;$me)"
Check 'no duplicates, SYSTEM not twice' (Get-StreamZhDataFolderSddl @('S-1-5-18', $me) @($me, '')) "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;$me)"
$refused = $false
try { [void](Get-StreamZhDataFolderSddl @() @('S-1-5-21-1)(A;OICI;FA;;;WD')) } catch { $refused = $true }
Check 'a value that is no SID is refused' $refused $true

Write-Host "$($script:count) checks, $($script:failures) failed"
exit $script:failures
