$ErrorActionPreference = 'Stop'
$updateId = [Guid]::ParseExact($env:CQT_UPDATE_ID, 'N').ToString('N')
$old = Get-Process -Id ([int]$env:CQT_UPDATE_PID)
$null = $old.Handle
$target = $old.MainModule.FileName
$folder = [IO.Path]::GetDirectoryName($target)
if ([IO.Path]::GetFileName($target) -ne 'CodexQuotaTray.exe') { throw 'Unexpected application path.' }
$stage = [IO.Path]::Combine($folder, ".codex-update-$updateId")
$payload = [IO.Path]::Combine($stage, 'CodexQuotaTray.exe')
$backup = [IO.Path]::Combine($stage, 'previous.exe')
$failureDir = [IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'CodexQuotaTray')
$failure = [IO.Path]::Combine($failureDir, 'last-update-failure.txt')
$boot = [Threading.EventWaitHandle]::new($false, 'ManualReset', "Local\CQT-boot-$updateId")
$ready = [Threading.EventWaitHandle]::OpenExisting("Local\CQT-helper-$updateId")
$swapped = $false
$oldExited = $false
$newProcess = $null
try {
    if (![IO.File]::Exists($payload)) { throw 'Missing prepared update.' }
    $null = $ready.Set()
    $oldExited = $old.WaitForExit(30000)
    if (!$oldExited) { throw 'Timed out waiting for the running app.' }
    for ($attempt = 0; ; $attempt++) {
        try { [IO.File]::Replace($payload, $target, $backup); break }
        catch [IO.IOException] {
            if ($attempt -ge 20) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
    $swapped = $true
    $newProcess = Start-Process -FilePath $target -WorkingDirectory $folder -WindowStyle Hidden -ArgumentList '--update-ready',$updateId -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (!$boot.WaitOne(250)) {
        if ($newProcess.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Updated application did not start.' }
    }
    [IO.File]::Delete($backup)
    if ([IO.File]::Exists($failure)) { Remove-Item -LiteralPath $failure -ErrorAction SilentlyContinue }
} catch {
    [IO.Directory]::CreateDirectory($failureDir) | Out-Null
    [IO.File]::WriteAllText($failure, $env:CQT_UPDATE_TAG)
    if ($swapped -and [IO.File]::Exists($backup)) {
        if ($newProcess -and !$newProcess.HasExited) { $newProcess.Kill(); $newProcess.WaitForExit() }
        [IO.File]::Replace($backup, $target, $payload)
    }
    if ($oldExited) { Start-Process -FilePath $target -WorkingDirectory $folder -WindowStyle Hidden }
} finally {
    $ready.Dispose()
    $boot.Dispose()
    if ([IO.File]::Exists($payload)) { [IO.File]::Delete($payload) }
    if (![IO.File]::Exists($backup)) { [IO.Directory]::Delete($stage) }
}
