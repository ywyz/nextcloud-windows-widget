# Run with PowerShell 7 on Windows from the repository root.
# Build tests/CoreChecks in Release before running this script.
# -Public drives the old build's actual settings dialog against the published Release.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OldExe,
    [Parameter(Mandatory)][string]$NewExe,
    [Parameter(Mandatory)][string]$Dotnet,
    [switch]$Public,
    [switch]$Rollback
)
$ErrorActionPreference = 'Stop'
$old = (Resolve-Path -LiteralPath $OldExe).Path
$new = (Resolve-Path -LiteralPath $NewExe).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$testDir = [IO.Directory]::CreateDirectory((Join-Path $env:TEMP ('widget-upgrade-' + [guid]::NewGuid().ToString('N')))).FullName
$exe = Join-Path $testDir 'NextcloudWidget-win-x64.exe'
$profile = Join-Path $testDir 'data'
$stage = Join-Path $testDir ('.nextcloud-widget-update-' + [guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $old -Destination $exe
& $Dotnet run -c Release --no-build --project (Join-Path $repo 'tests/CoreChecks') -- --seed-update $profile
if ($LASTEXITCODE) { throw 'Cannot seed pending task.' }

function Start-TestProcess([string]$file,[string[]]$arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $file
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    # This fixture controls every argument; none contains an embedded double quote.
    $start.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    [Diagnostics.Process]::Start($start)
}

function Invoke-SettingsButton([int]$processId,[string]$field,[string]$value,[int]$seconds) {
    $property = if ($field -eq 'Id') {
        [Windows.Automation.AutomationElement]::AutomationIdProperty
    } else { [Windows.Automation.AutomationElement]::NameProperty }
    $condition = New-Object Windows.Automation.PropertyCondition($property,$value)
    $processFilter = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$processId)
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$processFilter)
        foreach ($window in $windows) {
            $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
            if ($button -and $button.Current.IsEnabled) {
                $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                return
            }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "Missing application control: $value"
}

try {
    if ($Public) {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $app = Start-TestProcess $exe @('--data-dir',$profile)
        Invoke-SettingsButton $app.Id 'Id' 'SettingsButton' 30
        $ver = (Get-Item $new).VersionInfo.FileVersion -replace '\.0$',''
        # Automatic discovery may already have populated the install action.
        try { Invoke-SettingsButton $app.Id 'Name' '检查更新' 3 } catch {}
        Invoke-SettingsButton $app.Id 'Name' "安装 $ver" 100
        if (!$app.WaitForExit(240000)) { throw 'Old application stayed open.' }
    } else {
        $smoke = Start-TestProcess $exe @('--smoke-test','--data-dir',$profile)
        if (!$smoke.WaitForExit(45000) -or $smoke.ExitCode -ne 0) { throw 'Old executable failed its WPF smoke test.' }
        [void][IO.Directory]::CreateDirectory($stage)
        Copy-Item -LiteralPath $exe -Destination (Join-Path $stage 'installer.exe')
        Copy-Item -LiteralPath $new -Destination (Join-Path $stage 'NextcloudWidget-win-x64.exe')
        $testProfile = $profile
        if ($Rollback) {
            $testProfile = Join-Path $testDir 'invalid-profile'
            [IO.File]::WriteAllText($testProfile,'startup failure fixture')
        }
        $installer = Start-TestProcess (Join-Path $stage 'installer.exe') @('--apply-widget-update',"$($smoke.Id)",$exe,'--data-dir',$testProfile)
        if (!$installer.WaitForExit(120000)) { throw 'Installer did not exit.' }
        if (($installer.ExitCode -eq 0) -eq [bool]$Rollback) { throw "Installer returned $($installer.ExitCode)." }
    }
    $expected = if ($Rollback) { $old } else { $new }
    $expectedHash = (Get-FileHash $expected -Algorithm SHA256).Hash
    $deadline = [DateTime]::UtcNow.AddSeconds(100)
    do {
        if ((Get-FileHash $exe -Algorithm SHA256).Hash -eq $expectedHash -and
            !(Get-ChildItem -LiteralPath $testDir -Recurse -Filter previous.exe)) { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    if ((Get-FileHash $exe -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Installed binary hash differs.' }
    if (Get-ChildItem -LiteralPath $testDir -Recurse -Filter previous.exe) { throw 'New application never confirmed readiness.' }
    & $Dotnet run -c Release --no-build --project (Join-Path $repo 'tests/CoreChecks') -- --verify-update $profile
    if ($LASTEXITCODE) { throw 'Pending task did not survive the upgrade.' }
    Write-Output "PASS installed upgrade (public=$Public rollback=$Rollback) $testDir"
} finally {
    # Only close processes whose executable lives in this fixture's unique folder.
    Get-Process -Name 'NextcloudWidget-win-x64','installer' -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Path -and $_.Path.StartsWith(($testDir + '\'),[StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $_.Id -ErrorAction SilentlyContinue
        }
    }
}
