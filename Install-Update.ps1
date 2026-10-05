param([Parameter(Mandatory = $true)][string]$ManifestPath)
$ErrorActionPreference = 'Stop'
$applied = [Collections.Generic.List[string]]::new()
$resultPath = $null
$backupDirectory = $null
try {
    $request = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    foreach ($field in @('Package','TargetDirectory','ProcessId','UpdateDirectory')) {
        if ($null -eq $request.$field) { throw "The install request is missing $field." }
    }
    foreach ($field in @('Version','ArchivePath','SourceDirectory','Sha256')) {
        if ([string]::IsNullOrWhiteSpace($request.Package.$field)) { throw "The update package is missing $field." }
    }
    $updateDirectory = [IO.Path]::GetFullPath($request.UpdateDirectory)
    $updatePrefix = $updateDirectory + [IO.Path]::DirectorySeparatorChar
    $manifestFullPath = [IO.Path]::GetFullPath($ManifestPath)
    $sourceDirectory = [IO.Path]::GetFullPath($request.Package.SourceDirectory)
    $archivePath = [IO.Path]::GetFullPath($request.Package.ArchivePath)
    $targetDirectory = [IO.Path]::GetFullPath($request.TargetDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $targetPrefix = $targetDirectory + [IO.Path]::DirectorySeparatorChar
    $sourcePrefix = $sourceDirectory + [IO.Path]::DirectorySeparatorChar
    if (-not $manifestFullPath.StartsWith($updatePrefix,[StringComparison]::OrdinalIgnoreCase) -or
        -not $sourceDirectory.StartsWith($updatePrefix,[StringComparison]::OrdinalIgnoreCase) -or
        -not $archivePath.StartsWith($updatePrefix,[StringComparison]::OrdinalIgnoreCase) -or
        $targetDirectory.Equals([IO.Path]::GetPathRoot($targetDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase) -or
        $targetDirectory.StartsWith($updatePrefix,[StringComparison]::OrdinalIgnoreCase) -or
        $updateDirectory.StartsWith($targetPrefix,[StringComparison]::OrdinalIgnoreCase) -or
        $request.Package.Sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'The install request contains unsafe package or destination paths.' }
    $resultPath = Join-Path $updateDirectory 'install-result.json'
    $archiveStream = [IO.File]::OpenRead($archivePath)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $archiveDigest = [BitConverter]::ToString($hasher.ComputeHash($archiveStream)).Replace('-','') }
    finally { $archiveStream.Dispose(); $hasher.Dispose() }
    if ($archiveDigest -ne $request.Package.Sha256) { throw 'The downloaded ZIP changed after verification. Download the update again.' }
    $targetExe = Join-Path $targetDirectory 'ScreenLingo.exe'
    if (-not (Test-Path -LiteralPath $targetExe -PathType Leaf)) { throw "ScreenLingo.exe is missing from the install destination: $targetDirectory" }
    $runningProcess = [Diagnostics.Process]::GetProcesses() | Where-Object { $_.Id -eq $request.ProcessId }
    if ($null -ne $runningProcess) {
        if (-not $runningProcess.MainModule.FileName.Equals($targetExe,[StringComparison]::OrdinalIgnoreCase)) { throw 'The supplied process does not belong to this ScreenLingo installation.' }
        if (-not $runningProcess.WaitForExit(60000)) { throw 'ScreenLingo did not close within 60 seconds. Close it before installing again.' }
        $runningProcess.Dispose()
    }
    $files = @(Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File)
    if ($files.Count -eq 0) { throw 'The verified update folder is empty.' }
    $backupDirectory = Join-Path $updateDirectory ('previous-' + [Guid]::NewGuid().ToString('N'))
    foreach ($file in $files) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "The update contains a linked file: $($file.FullName)" }
        $relativePath = $file.FullName.Substring($sourcePrefix.Length)
        $destinationPath = [IO.Path]::GetFullPath((Join-Path $targetDirectory $relativePath))
        if (-not $destinationPath.StartsWith($targetPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe install path: $destinationPath" }
        $parentPath = $destinationPath
        while ($parentPath.StartsWith($targetPrefix,[StringComparison]::OrdinalIgnoreCase)) {
            if (Test-Path -LiteralPath $parentPath) {
                if (((Get-Item -LiteralPath $parentPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "The install destination contains a link: $parentPath" }
            }
            $parentPath = [IO.Path]::GetDirectoryName($parentPath)
        }
        if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
            $backupPath = Join-Path $backupDirectory $relativePath
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backupPath)) -Force | Out-Null
            Copy-Item -LiteralPath $destinationPath -Destination $backupPath
        }
    }
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($sourcePrefix.Length)
        $destinationPath = [IO.Path]::GetFullPath((Join-Path $targetDirectory $relativePath))
        if (-not $destinationPath.StartsWith($targetPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe install path: $destinationPath" }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destinationPath)) -Force | Out-Null
        $applied.Add($relativePath)
        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
    }
    $installedVersion = (Get-Item -LiteralPath (Join-Path $targetDirectory 'ScreenLingo.dll')).VersionInfo.ProductVersion
    if ($installedVersion -ne $request.Package.Version) { throw "Installed version $installedVersion does not match $($request.Package.Version)." }
    $restarted = Start-Process -FilePath $targetExe -WindowStyle Normal -PassThru
    [PSCustomObject]@{ Status='installed'; Version=$installedVersion; ProcessId=$restarted.Id; BackupDirectory=$backupDirectory } |
        ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    $readyPath = Join-Path $updateDirectory 'ready.json'
    if (Test-Path -LiteralPath $readyPath -PathType Leaf) { Remove-Item -LiteralPath $readyPath }
    exit 0
} catch {
    $failure = $_.Exception.ToString()
    foreach ($relativePath in $applied) {
        try {
            $destinationPath = [IO.Path]::GetFullPath((Join-Path $targetDirectory $relativePath))
            if (-not $destinationPath.StartsWith($targetPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe rollback path: $destinationPath" }
            $backupPath = Join-Path $backupDirectory $relativePath
            if (Test-Path -LiteralPath $backupPath -PathType Leaf) { Copy-Item -LiteralPath $backupPath -Destination $destinationPath -Force }
            elseif (Test-Path -LiteralPath $destinationPath -PathType Leaf) { Remove-Item -LiteralPath $destinationPath }
        } catch { $failure += "`nRestore failed for ${relativePath}: $($_.Exception.ToString())" }
    }
    if ($null -ne $resultPath) {
        [PSCustomObject]@{ Status='failed'; Error=$failure } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    }
    Add-Type -AssemblyName PresentationFramework
    [Windows.MessageBox]::Show("ScreenLingo could not install the update. Previous files were restored where possible.`n`n$failure",'ScreenLingo update failed','OK','Error') | Out-Null
    exit 1
}
