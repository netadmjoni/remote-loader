[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Framework = "net8.0-windows10.0.19041",
    [string]$Version = "0.1.2"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$solutionPath = Join-Path $repoRoot "WgbDiagnostics.sln"
$appProjectPath = Join-Path $repoRoot "src\WgbDiagnostics.App\WgbDiagnostics.App.csproj"
$installerProjectPath = Join-Path $repoRoot "installer\WgbDiagnostics.Installer\WgbDiagnostics.Installer.wixproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\WgbDiagnostics.App"
$installerArtifactsDir = Join-Path $repoRoot "artifacts\installer"
$installerIntermediateDir = Join-Path $repoRoot "installer\WgbDiagnostics.Installer\obj"
$harvestedWxsPath = Join-Path $installerIntermediateDir "HarvestedFiles.wxs"
$expectedMsiName = "WgbDiagnostics-$Version-win-x64.msi"
$builtMsiPath = Join-Path $repoRoot "installer\WgbDiagnostics.Installer\bin\x64\$Configuration\$expectedMsiName"
$finalMsiPath = Join-Path $installerArtifactsDir $expectedMsiName

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-UnderRepo {
    param([Parameter(Mandatory)][string]$Path)

    $fullRepo = Resolve-FullPath $repoRoot
    $fullPath = Resolve-FullPath $Path
    if (!$fullPath.StartsWith($fullRepo, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside repository root: $fullPath"
    }
}

function Reset-Directory {
    param([Parameter(Mandatory)][string]$Path)

    Assert-UnderRepo $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE. Arguments: $($Arguments -join ' ')"
    }
}

function New-WixId {
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][string]$Value
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
        $hash = $sha.ComputeHash($bytes)
        $hex = -join ($hash[0..15] | ForEach-Object { $_.ToString("x2") })
        return "$Prefix$hex"
    }
    finally {
        $sha.Dispose()
    }
}

function Write-WixDirectory {
    param(
        [Parameter(Mandatory)][System.Xml.XmlWriter]$Writer,
        [Parameter(Mandatory)][System.IO.DirectoryInfo]$Directory,
        [Parameter(Mandatory)][AllowEmptyString()][string]$RelativePath,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$ComponentIds
    )

    $subDirectories = Get-ChildItem -LiteralPath $Directory.FullName -Directory | Sort-Object FullName
    foreach ($subDirectory in $subDirectories) {
        $childRelativePath = if ([string]::IsNullOrWhiteSpace($RelativePath)) {
            $subDirectory.Name
        }
        else {
            Join-Path $RelativePath $subDirectory.Name
        }

        $Writer.WriteStartElement("Directory")
        $Writer.WriteAttributeString("Id", (New-WixId "dir_" $childRelativePath))
        $Writer.WriteAttributeString("Name", $subDirectory.Name)
        Write-WixDirectory -Writer $Writer -Directory $subDirectory -RelativePath $childRelativePath -ComponentIds $ComponentIds
        $Writer.WriteEndElement()
    }

    $files = Get-ChildItem -LiteralPath $Directory.FullName -File | Sort-Object FullName
    foreach ($file in $files) {
        $relativeFilePath = if ([string]::IsNullOrWhiteSpace($RelativePath)) {
            $file.Name
        }
        else {
            Join-Path $RelativePath $file.Name
        }
        $componentId = New-WixId "cmp_" $relativeFilePath
        $fileId = New-WixId "fil_" $relativeFilePath
        $ComponentIds.Add($componentId)

        $Writer.WriteStartElement("Component")
        $Writer.WriteAttributeString("Id", $componentId)
        $Writer.WriteAttributeString("Guid", "*")
        $Writer.WriteStartElement("File")
        $Writer.WriteAttributeString("Id", $fileId)
        $Writer.WriteAttributeString("Source", $file.FullName)
        $Writer.WriteAttributeString("KeyPath", "yes")
        $Writer.WriteEndElement()
        $Writer.WriteEndElement()
    }
}

function Write-HarvestedWxs {
    param(
        [Parameter(Mandatory)][string]$PublishDirectory,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $publishFiles = @(Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse | Sort-Object FullName)
    if ($publishFiles.Count -eq 0) {
        throw "Publish directory contains no files: $PublishDirectory"
    }

    $outputDirectory = [System.IO.Path]::GetDirectoryName($OutputPath)
    if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
        throw "Could not resolve harvested WiX output directory."
    }

    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

    $componentIds = [System.Collections.Generic.List[string]]::new()
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)

    $writer = [System.Xml.XmlWriter]::Create($OutputPath, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement("Wix", "http://wixtoolset.org/schemas/v4/wxs")

        $writer.WriteStartElement("Fragment")
        $writer.WriteStartElement("DirectoryRef")
        $writer.WriteAttributeString("Id", "INSTALLFOLDER")
        Write-WixDirectory -Writer $writer -Directory ([System.IO.DirectoryInfo]$PublishDirectory) -RelativePath "" -ComponentIds $componentIds
        $writer.WriteEndElement()
        $writer.WriteEndElement()

        $writer.WriteStartElement("Fragment")
        $writer.WriteStartElement("ComponentGroup")
        $writer.WriteAttributeString("Id", "PublishedFiles")
        foreach ($componentId in $componentIds) {
            $writer.WriteStartElement("ComponentRef")
            $writer.WriteAttributeString("Id", $componentId)
            $writer.WriteEndElement()
        }

        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }

    return $publishFiles.Count
}

Reset-Directory $publishDir
Reset-Directory $installerArtifactsDir
Reset-Directory $installerIntermediateDir

Write-Host "Restoring solution..."
Invoke-Checked dotnet @("restore", $solutionPath)

Write-Host "Restoring installer project..."
Invoke-Checked dotnet @("restore", $installerProjectPath)

Write-Host "Building solution..."
Invoke-Checked dotnet @("build", $solutionPath, "-c", $Configuration, "-p:Platform=x64", "--no-restore")

Write-Host "Running tests..."
Invoke-Checked dotnet @("test", $solutionPath, "-c", $Configuration, "-p:Platform=x64", "--no-restore")

Write-Host "Publishing self-contained $Runtime application..."
Invoke-Checked dotnet @("publish", $appProjectPath, "-c", $Configuration, "-r", $Runtime, "-f", $Framework, "--self-contained", "true", "-p:Platform=x64", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-o", $publishDir)

if (Test-Path -LiteralPath (Join-Path $publishDir "appsettings.json")) {
    throw "appsettings.json must not be published into Program Files payload."
}

$publishedFileCount = Write-HarvestedWxs -PublishDirectory $publishDir -OutputPath $harvestedWxsPath
Write-Host "Harvested $publishedFileCount published files."

Write-Host "Building MSI..."
Invoke-Checked dotnet @("build", $installerProjectPath, "-c", $Configuration, "-p:Platform=x64", "-p:ProductVersion=$Version", "-p:HarvestedWxs=$harvestedWxsPath", "--no-restore")

if (!(Test-Path -LiteralPath $builtMsiPath)) {
    throw "Expected MSI was not created: $builtMsiPath"
}

Copy-Item -LiteralPath $builtMsiPath -Destination $finalMsiPath -Force
$msi = Get-Item -LiteralPath $finalMsiPath
$hash = Get-FileHash -LiteralPath $finalMsiPath -Algorithm SHA256

Write-Host "MSI created:"
Write-Host "  Path: $($msi.FullName)"
Write-Host "  Size: $($msi.Length) bytes"
Write-Host "  SHA-256: $($hash.Hash)"
