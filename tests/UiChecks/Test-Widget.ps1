#requires -Version 7.4
# Exercises an installed Windows x64 EXE with a synthetic profile and loopback model.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable, [string]$Python = 'python')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WidgetTestWindows {
    private delegate bool Visitor(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visitor visit, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);

    public static IntPtr Find(uint process, string title) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, state) => {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != process) return true;
            var caption = new StringBuilder(256);
            GetWindowText(window, caption, caption.Capacity);
            if (caption.ToString() != title) return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }
}
'@
$ui = [Windows.Automation.AutomationElement]
$tree = [Windows.Automation.TreeScope]::Descendants
$root = [IO.Directory]::CreateTempSubdirectory('widget-ui-check-').FullName
$appPath = (Resolve-Path -LiteralPath $Executable).Path
$process = $null
$server = $null

function Start-FixtureProcess($path, [string[]]$arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($path)
    $info.UseShellExecute = $false
    $info.WindowStyle = 'Hidden'
    foreach ($arg in $arguments) { $info.ArgumentList.Add($arg) }
    return [Diagnostics.Process]::Start($info)
}
function Await($description, [scriptblock]$query) {
    $until = [DateTime]::UtcNow.AddSeconds(18)
    do {
        try { $value = & $query; if ($value) { return $value } }
        catch [Windows.Automation.ElementNotAvailableException] {}
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for $description"
}
function Item($scope, [string]$id) {
    $scope.FindFirst($tree, [Windows.Automation.PropertyCondition]::new($ui::AutomationIdProperty, $id))
}
function Named($scope, [string]$name) {
    $scope.FindFirst($tree, [Windows.Automation.PropertyCondition]::new($ui::NameProperty, $name))
}
function Window([string]$name) {
    Await $name {
        $handle = [WidgetTestWindows]::Find($process.Id, $name)
        if ($handle -ne [IntPtr]::Zero) { $ui::FromHandle($handle) }
    }
}
function Click($control) { $control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-Row($control) { $control.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Set-Field($control,$value) { $control.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Value($control) { $control.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value }
function MailRows($window) {
    (Item $window 'MailList').FindAll($tree, [Windows.Automation.PropertyCondition]::new(
        $ui::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))
}
function MailCheckbox($window, $subject) { Named (Item $window 'MailList') "选择邮件 $subject" }
function Checked($control) {
    $control.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq
        [Windows.Automation.ToggleState]::On
}
function Stop-FixtureProcess($child) {
    if ($child -and !$child.HasExited) { $child.Kill(); $child.WaitForExit() }
}

try {
    $server = Start-FixtureProcess $Python @((Join-Path $PSScriptRoot 'fixture.py'),$root)
    $port = Await 'loopback server' { if (Test-Path "$root/fixture-port") { [IO.File]::ReadAllText("$root/fixture-port") } }
    $process = Start-FixtureProcess $appPath @('--data-dir',$root)
    $main = Window 'Nextcloud 小窗'

    $theme = Await 'theme switch' { Item $main 'ThemeButton' }
    Click $theme
    $null = Await 'night theme persisted' {
        (Get-Content "$root/window.json" -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json).DarkMode
    }
    $quick = Await 'quick task entry' { Item $main 'QuickTitle' }
    Set-Field $quick 'UI acceptance task'
    Click (Item $main 'CreateButton')
    $null = Await 'inline task in list' { Named (Item $main 'TasksList') 'UI acceptance task' }
    if ((Value $quick) -ne '') { throw 'Quick entry did not reset.' }
    Write-Output 'PASS inline task entry in the main window'

    Click (Item $main 'SettingsButton')
    $settings = Window '设置'
    Click (Item $settings 'TranslationSettingsButton')
    $config = Window '邮件翻译设置'
    Set-Field (Item $config 'TranslationEndpoint') "http://127.0.0.1:$port/v1"
    Set-Field (Item $config 'TranslationModel') 'fixture-translator'
    Set-Field (Item $config 'TranslationLanguage') '简体中文'
    Click (Item $config 'SaveTranslationSettings')
    Click (Named $settings '关闭')

    Select-Row (Item $main 'MailTab')
    $null = Await 'three v0.1 cached messages' { @(MailRows $main).Count -eq 3 }
    $null = Await 'initial unread badge' { Named (@(MailRows $main)[0]) '未读' }
    Select-Row (@(MailRows $main)[0])
    Click (Item $main 'ReadButton')
    $reader = Window '阅读邮件'
    $body = Value (Item $reader 'MailOriginalBody')
    if (!$body.StartsWith('Only this message body')) { throw 'Expected original offline mail body.' }
    if (Test-Path "$root/model-request.json") { throw 'Opened mail was sent to the model automatically.' }
    Click (Item $reader 'TranslateMail')
    $null = Await 'translated mail' {
        $box = Item $reader 'MailTranslatedBody'
        $box -and (Value $box) -eq '只有这封邮件的正文需要翻译。'
    }
    $request = Get-Content -Raw -LiteralPath "$root/model-request.json" | ConvertFrom-Json
    if ($request.model -ne 'fixture-translator' -or $request.messages[-1].content -ne $body) {
        throw 'Wrong translation model or text.'
    }
    Click (Named $reader '关闭')
    $null = Await 'read-on-open' { !(Named (@(MailRows $main)[0]) '未读') }
    Write-Output 'PASS manual-only translation and read-on-open'

    $select = Item $main 'SelectMailButton'
    Click $select
    $null = Await 'three selected messages' {
        (Checked (MailCheckbox $main 'QA message 1')) -and (Checked (MailCheckbox $main 'QA message 2')) -and
            (Checked (MailCheckbox $main 'QA message 3'))
    }
    Click (Item $main 'MarkReadButton')
    $null = Await 'all messages read' { !(Named (Item $main 'MailList') '未读') }
    $null = Await 'bulk action complete' { (Item $main 'SelectMailButton').Current.IsEnabled }
    # Keep the middle message by clearing only its checkbox.
    (MailCheckbox $main 'QA message 2').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    $null = Await 'two checked messages' {
        (Checked (MailCheckbox $main 'QA message 1')) -and !(Checked (MailCheckbox $main 'QA message 2')) -and
            (Checked (MailCheckbox $main 'QA message 3'))
    }
    $trash = Item $main 'TrashButton'
    Click $trash
    $confirm = Window '移到废纸篓'
    if (!(Named $confirm '将选中的 2 封邮件移到废纸篓？离线时会在下次连线后同步。')) { throw 'Incorrect selection in confirmation.' }
    Click (Item $confirm 'CancelAction')
    $null = Await 'cancelled confirmation closed' { $main.Current.IsEnabled }
    if (@(MailRows $main).Count -ne 3) { throw 'Cancel removed mail.' }
    Click (Item $main 'TrashButton')
    Click (Item (Window '移到废纸篓') 'ConfirmAction')
    $null = Await 'trash pending locally' { @(MailRows $main).Count -eq 1 }
    if (!(Named (Item $main 'MailList') 'QA message 2')) { throw 'Wrong message retained.' }

    Stop-FixtureProcess $process
    $process = Start-FixtureProcess $appPath @('--data-dir',$root)
    $main = Window 'Nextcloud 小窗'
    Click (Item $main 'ThemeButton')
    $null = Await 'light theme persisted' {
        !(Get-Content "$root/window.json" -Raw | ConvertFrom-Json).DarkMode
    }
    Select-Row (Item $main 'MailTab')
    $null = Await 'mail list after restart' { Item $main 'MailList' }
    if (@(MailRows $main).Count -ne 1 -or !(Named (Item $main 'MailList') 'QA message 2')) {
        throw 'Offline mail state was lost on restart.'
    }
    Write-Output "PASS selection, bulk read, cancel and confirm trash, restart. Profile: $root"
} finally {
    Stop-FixtureProcess $process
    Stop-FixtureProcess $server
}
