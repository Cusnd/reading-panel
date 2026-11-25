[CmdletBinding(DefaultParameterSetName = "Legacy")]
param(
    [string]$Python = "python",
    [string]$PiperCache,
    [Parameter(ParameterSetName = "Legacy")][switch]$Compact,
    [Parameter(Mandatory, ParameterSetName = "Editions")]
    [ValidateSet("Online", "Offline", "Both")][string]$Edition
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = [IO.Directory]::CreateDirectory((Join-Path $root "dist")).FullName
$stageName = ".publish-" + [guid]::NewGuid().ToString("N")
$stage = [IO.Directory]::CreateDirectory((Join-Path $dist $stageName)).FullName

function Install-OutputFile([string]$source, [string]$destination) {
    if ([IO.File]::Exists($destination)) {
        [IO.File]::Replace($source, $destination, [System.Management.Automation.Language.NullString]::Value)
    } else {
        [IO.File]::Move($source, $destination)
    }
}

function Publish-Panel([string]$variant) {
    $online = $variant -eq "Online"
    $standalone = $variant -eq "Standalone"
    $suffix = if ($variant -in "Online", "Offline") { "-global-" + $variant.ToLowerInvariant() }
        elseif (!$standalone) { "-" + $variant.ToLowerInvariant() }
    $folder = "ReadingPanel$suffix"
    $publishDir = Join-Path $stage $folder
    $archiveName = "ReadingPanel-win-x64$suffix.zip"
    $destination = Join-Path $dist $folder
    $installedExe = Join-Path $destination "ReadingPanel.exe"
    if ([IO.File]::Exists($installedExe)) {
        try { [IO.File]::Open($installedExe, 'Open', 'ReadWrite', 'None').Dispose() }
        catch [IO.IOException] { throw "Close $installedExe before updating its package. $($_.Exception.Message)" }
    }

    if (!$online) {
        & $Python tools/build_dictionary.py
        if ($LASTEXITCODE -ne 0) { throw "Could not build the offline dictionary." }
    }
    $runtime = if ($standalone) {
        "--self-contained", "true", "-p:EnableCompressionInSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true"
    } else { "--self-contained", "false" }
    dotnet publish src/ReadingPanel/ReadingPanel.csproj -c Release -r win-x64 @runtime -o $publishDir "-p:OnlineEdition=$online" -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publishing Reading Panel $variant failed." }

    $required = @("ReadingPanel.exe")
    if ($online) {
        $required += "licenses/HtmlAgilityPack-LICENSE.txt"
        if (Test-Path -LiteralPath (Join-Path $publishDir "assets/ecdict.sqlite3")) { throw "Unexpected offline dictionary in online package." }
    } else {
        $required += "assets/ecdict.sqlite3", "assets/ECDICT-LICENSE.txt"
        $voiceDir = Join-Path $publishDir "piper-john-v1"
        $voiceArgs = @("--prepare-piper", $voiceDir)
        if ($PiperCache) { $voiceArgs += (Resolve-Path -LiteralPath $PiperCache).Path }
        $voiceBuild = Start-Process -FilePath (Join-Path $publishDir "ReadingPanel.exe") -ArgumentList ($voiceArgs | ForEach-Object { '"' + $_ + '"' }) -WindowStyle Hidden -PassThru -Wait
        if ($voiceBuild.ExitCode -ne 0) { throw "Could not prepare the verified Piper John voice pack." }
        $required += "piper-john-v1/piper.exe", "piper-john-v1/en_US-john-medium.onnx"
    }
    foreach ($file in $required) {
        if (!(Test-Path -LiteralPath (Join-Path $publishDir $file) -PathType Leaf)) { throw "$variant package is missing $file." }
    }

    if ($variant -eq "Offline") {
        $sevenZip = (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source
        if (!$sevenZip) { $sevenZip = Join-Path $env:ProgramFiles "7-Zip/7z.exe" }
        if (!(Test-Path -LiteralPath $sevenZip)) { throw "Install 7-Zip to build the compressed offline ZIP." }
        & $Python tools/build_dictionary.py --compact-copy (Join-Path $publishDir "assets/ecdict.sqlite3")
        if ($LASTEXITCODE -ne 0) { throw "Could not compact the offline index." }
        Push-Location -LiteralPath $stage
        try {
            # Windows tar supports PPMd in ZIP for this dictionary.
            & $sevenZip a -tzip -mm=PPMd -mo=8 -mmem=128m -mmt=1 -bso0 -bsp0 $archiveName "$folder/assets"
            if ($LASTEXITCODE -ne 0) { throw "Compressing the offline dictionary failed." }
            & $sevenZip a -tzip -mm=Deflate -mx=5 -bso0 -bsp0 $archiveName "$folder/ReadingPanel.exe" "$folder/licenses" "$folder/piper-john-v1"
            if ($LASTEXITCODE -ne 0) { throw "Creating the offline archive failed." }
        } finally { Pop-Location }
    } else {
        Compress-Archive -LiteralPath $publishDir -DestinationPath (Join-Path $stage $archiveName)
    }
    # Replace complete files so a failed upgrade cannot leave a truncated executable or SQLite index.
    foreach ($file in Get-ChildItem -LiteralPath $publishDir -File -Recurse) {
        $target = Join-Path $destination $file.FullName.Substring($publishDir.Length + 1)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Install-OutputFile $file.FullName $target
    }
    Install-OutputFile (Join-Path $stage $archiveName) (Join-Path $dist $archiveName)
    Get-Item -LiteralPath (Join-Path $dist $archiveName) | Select-Object FullName, Length
}

Push-Location -LiteralPath $root
try {
    if ($Edition -eq "Both") { Publish-Panel "Online"; Publish-Panel "Offline" }
    elseif ($Edition) { Publish-Panel $Edition }
    elseif ($Compact) { Publish-Panel "Compact" }
    else { Publish-Panel "Standalone" }
} finally {
    Pop-Location
    $stagingDir = Get-Item -LiteralPath $stage
    if ($stagingDir.Parent.FullName -eq $dist -and $stagingDir.Name -eq $stageName -and
        !($stagingDir.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $stagingDir.FullName -Recurse -Force
    }
}
