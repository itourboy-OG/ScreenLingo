<#
.SYNOPSIS
Builds and checks the current ScreenLingo version in a separate package directory.
.DESCRIPTION
PackageDirectory must be an explicit output directory outside this source folder.
Existing Desktop and installed application copies are not replaced. Installation is left to the user.
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
Write-Output "ScreenLingo package is ready for manual installation: $executablePath"
