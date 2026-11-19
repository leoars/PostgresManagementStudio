param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageTools.ps1')
$verified = Test-PgmsPackage $PackagePath
if ($verified.Mode -ne 'SelfContained') { throw 'This smoke requires a self-contained package.' }
$package = (Get-Item -LiteralPath $PackagePath).FullName
$state = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/launch-checks/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $state -Force | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $package 'PGMS.exe'))
$start.UseShellExecute = $false
$start.WorkingDirectory = $package
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.Environment['PGMS_STATE_DIRECTORY'] = $state
# The interface follows the Windows language by default; English keeps the Connect to Server title below the same on every machine.
$start.Environment['PGMS_LANGUAGE'] = 'en'
# Point runtime discovery at an empty directory; module inspection below is the decisive evidence.
$emptyRuntime = Join-Path $state 'empty-runtime'
New-Item -ItemType Directory -Path $emptyRuntime | Out-Null
$start.Environment['DOTNET_ROOT'] = $emptyRuntime
$start.Environment['DOTNET_ROOT_X64'] = $emptyRuntime
$start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$started = $false
try {
    $started = $process.Start()
    if (!$started) { throw 'Packaged executable did not start.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) { throw "Packaged executable exited before showing a window: $($process.ExitCode)" }
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero -or $process.MainWindowTitle -notlike '*PGMS*') { throw 'PGMS main window was not observed.' }
    $runtime = @($process.Modules | Where-Object ModuleName -IEQ 'coreclr.dll')
    if ($runtime.Count -ne 1 -or $runtime[0].FileName -ine (Join-Path $package 'coreclr.dll')) { throw 'Process did not load the bundled runtime.' }
    $runtimePath = $runtime[0].FileName
    # PGMS opens Connect to Server at startup, as SSMS does, once the main window is idle. While that modal dialog is open the
    # main window ignores a close request, so wait for it (up to 10 s) and cancel it through UI Automation before closing.
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $mainElement = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $windowCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Window)
    $dialogDeadline = [DateTime]::UtcNow.AddSeconds(10)
    $startupDialogClosed = $false
    do {
        Start-Sleep -Milliseconds 200
        foreach ($dialog in $mainElement.FindAll([Windows.Automation.TreeScope]::Children, $windowCondition)) {
            if ($dialog.Current.Name -ne 'Connect to Server') { continue }
            $dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
            $startupDialogClosed = $true
        }
    } while (!$startupDialogClosed -and [DateTime]::UtcNow -lt $dialogDeadline)
    $closeDeadline = [DateTime]::UtcNow.AddSeconds(15)
    $closeRequested = $false
    do {
        if ($process.CloseMainWindow()) { $closeRequested = $true; break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $closeDeadline)
    if (!$closeRequested -or !$process.WaitForExit(15000)) { throw 'Packaged executable did not close gracefully.' }
    if ($process.ExitCode -ne 0) { throw "Packaged executable failed: $($process.ExitCode)" }
    if (!(Test-Path -LiteralPath (Join-Path $state 'layout.xml'))) { throw 'Isolated layout was not persisted on close.' }
    if (Test-Path -LiteralPath (Join-Path $state 'connections.json')) { throw 'Unexpected connection profile created by blank launch.' }
    $report = [pscustomobject]@{ Milestone = $verified.Milestone; PackagePath = $package; RuntimePath = $runtimePath; IsolatedState = $state; MainWindowObserved = $true; ExitCode = $process.ExitCode; Valid = $true }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $state 'launch-result.json') -Encoding utf8NoBOM
    $report
} finally {
    if ($started -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}
