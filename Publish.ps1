<#
.SYNOPSIS
Builds the current ScreenLingo version and refreshes Desktop\ScreenLingo.
.DESCRIPTION
PackageDirectory must be an explicit output directory outside this source folder.
Close the Desktop copy before publishing. Supporting files stay beside the EXE.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$sourceDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$packagePath = [IO.Path]::GetFullPath($PackageDirectory)
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$desktopAppPath = [IO.Path]::GetFullPath((Join-Path $desktopDirectory 'ScreenLingo'))
if ($packagePath.Equals($sourceDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $packagePath.StartsWith($sourceDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $sourceDirectory.StartsWith($packagePath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $packagePath.Equals($desktopAppPath, [StringComparison]::OrdinalIgnoreCase) -or
    $packagePath.StartsWith($desktopAppPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $desktopAppPath.StartsWith($packagePath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose a package output directory outside the source folder and separate from Desktop\ScreenLingo.'
}
$runningApps = @(Get-Process -Name 'ScreenLingo','ScreenLingo' -ErrorAction SilentlyContinue)
foreach ($runningApp in $runningApps) {
    if ($runningApp.Path -and $runningApp.Path.StartsWith($desktopAppPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Close ScreenLingo before updating Desktop\ScreenLingo, then run Publish.ps1 again.'
    }
}
$projectPath = Join-Path $sourceDirectory 'ScreenLingo.csproj'
New-Item -ItemType Directory -Path $packagePath -Force | Out-Null
& powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $sourceDirectory 'Build-Icon.ps1') -SourceDirectory $sourceDirectory -LogoPath (Join-Path ([IO.Path]::GetDirectoryName($packagePath)) 'ScreenLingo-logo.png')
if ($LASTEXITCODE -ne 0) { throw "Icon generation failed with exit code $LASTEXITCODE. Desktop copy was not updated." }
& dotnet restore $projectPath --locked-mode --configfile (Join-Path $sourceDirectory 'NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw "Dependency restore failed with exit code $LASTEXITCODE. Desktop copy was not updated." }
& dotnet publish $projectPath -c Release -r win-x64 --self-contained true --no-restore -o $packagePath --nologo -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE. Desktop copy was not updated." }
$executablePath = Join-Path $packagePath 'ScreenLingo.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw "The published EXE is missing: $executablePath" }
$checkDirectory = Join-Path ([IO.Path]::GetDirectoryName($packagePath)) 'screen-lingo-appearance-check'
$checkProcess = Start-Process -FilePath $executablePath -ArgumentList @('--appearance-check', ('"' + $checkDirectory + '"')) -WindowStyle Hidden -PassThru -Wait
if ($checkProcess.ExitCode -ne 0) { throw "Appearance check failed with exit code $($checkProcess.ExitCode). Read $checkDirectory\appearance-report.json. Desktop copy was not updated." }
New-Item -ItemType Directory -Path $desktopAppPath -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $packagePath) {
    Copy-Item -LiteralPath $item.FullName -Destination $desktopAppPath -Recurse -Force
}
$copiedExecutable = Join-Path $desktopAppPath 'ScreenLingo.exe'
if ((Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $copiedExecutable -Algorithm SHA256).Hash) {
    throw "Desktop EXE verification failed: $copiedExecutable"
}
Write-Output "ScreenLingo is ready: $copiedExecutable"
