#requires -Version 7.0
<#
.SYNOPSIS
Builds the branded ScreenLingo Setup EXE using a verified, project-local Inno Setup compiler.
.DESCRIPTION
Run Publish.ps1 first. Official build-tool downloads are pinned by SHA-256 and Authenticode publisher.
#>
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$InstallerOutputDirectory
)
$ErrorActionPreference = 'Stop'

function Get-VerifiedBuildDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Sha256,
        [Parameter(Mandatory = $true)][string]$Publisher
    )
    if (-not (Test-Path -LiteralPath $Destination -PathType Leaf)) {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try { Invoke-WebRequest -Uri $Uri -OutFile $Destination -TimeoutSec 90; break }
            catch [System.Net.Http.HttpRequestException], [Microsoft.PowerShell.Commands.HttpResponseException], [System.Threading.Tasks.TaskCanceledException] {
                if ($attempt -eq 3) { throw }
                Write-Warning (@{Event='installer_tool_download_retry';Uri=$Uri;Attempt=$attempt;Error=$_.Exception.Message} | ConvertTo-Json -Compress)
                Start-Sleep -Seconds $attempt
            }
        }
    }
    $actualHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    if ($actualHash -ne $Sha256) { throw "Build download verification failed: URL=$Uri; file=$Destination; expected SHA-256=$Sha256; received=$actualHash." }
    $signature = Get-AuthenticodeSignature -LiteralPath $Destination
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike "*CN=$Publisher,*") {
        throw "Build download signature failed: URL=$Uri; file=$Destination; status=$($signature.Status); expected publisher=$Publisher."
    }
}

$sourcePath = [IO.Path]::GetFullPath($PSScriptRoot)
$packagePath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$outputPath = [IO.Path]::GetFullPath($InstallerOutputDirectory)
if ($outputPath -eq $packagePath -or $outputPath.StartsWith($packagePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an installer output directory outside the published application folder.'
}
$executablePath = Join-Path $packagePath 'ScreenLingo.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw "The published application is missing: $executablePath. Run Publish.ps1 first." }
[string]$version = (Get-Item -LiteralPath $executablePath).VersionInfo.ProductVersion
[xml]$project = Get-Content -Raw -LiteralPath (Join-Path $sourcePath 'ScreenLingo.csproj')
if ($version -ne $project.Project.PropertyGroup.Version) { throw "Published application version=$version does not match project version=$($project.Project.PropertyGroup.Version). Rebuild the application first." }
$toolsPath = Join-Path $sourcePath '.tools'
New-Item -ItemType Directory -Path $toolsPath -Force | Out-Null
$compilerDownload = Join-Path $toolsPath 'innosetup-7.1.0-x64.exe'
Get-VerifiedBuildDownload -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -Destination $compilerDownload -Sha256 '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F' -Publisher 'Pyrsys B.V.'
$compilerPath = Join-Path $toolsPath 'inno-setup-7.1.0'
$compilerExe = Join-Path $compilerPath 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compilerExe -PathType Leaf)) {
    $bootstrap = Start-Process -FilePath $compilerDownload -ArgumentList @('/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="' + $compilerPath + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($bootstrap.ExitCode -ne 0) { throw "Project-local Inno Setup preparation failed: exit code=$($bootstrap.ExitCode); directory=$compilerPath." }
}
$runtimePath = Join-Path $toolsPath 'vc_redist.x64.exe'
Get-VerifiedBuildDownload -Uri 'https://download.visualstudio.microsoft.com/download/pr/bd1c8d9d-ba95-4eee-bc6e-df1fcc876373/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe' -Destination $runtimePath -Sha256 'CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B' -Publisher 'Microsoft Corporation'

$imagePath = Join-Path $sourcePath 'assets\installer-welcome.png'
if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) { throw "The ScreenLingo installer artwork is missing: $imagePath." }
Copy-Item -LiteralPath $imagePath -Destination (Join-Path $packagePath 'assets\installer-welcome.png') -Force
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
& $compilerExe /Q ('/DPackageDirectory=' + $packagePath) ('/DInstallerOutputDirectory=' + $outputPath) ('/DRuntimePath=' + $runtimePath) (Join-Path $sourcePath 'installer\ScreenLingo.iss')
if ($LASTEXITCODE -ne 0) { throw "ScreenLingo installer compilation failed: exit code=$LASTEXITCODE." }
$installerPath = Join-Path $outputPath ('ScreenLingo-' + $version + '-Setup-x64.exe')
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw "The compiler did not produce the expected installer: $installerPath." }
$desktopFolder = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ScreenLingo'
New-Item -ItemType Directory -Path $desktopFolder -Force | Out-Null
Copy-Item -LiteralPath $installerPath -Destination (Join-Path $desktopFolder 'ScreenLingo Setup.exe') -Force
Write-Output "ScreenLingo Setup is ready: $installerPath"
