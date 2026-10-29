<#
.SYNOPSIS
    PowerShell launcher for embed_apk.py
.DESCRIPTION
    Embeds an APK into a carrier file or extracts it back with SHA-256 integrity verification.
.EXAMPLE
    .\embed_apk.ps1
    .\embed_apk.ps1 embed -c "photo.jpg" -a "app.apk"
    .\embed_apk.ps1 extract -c "photo_embedded.jpg"
    .\embed_apk.ps1 info -c "photo_embedded.jpg"
    .\embed_apk.ps1 strip -c "photo_embedded.jpg"
#>
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    $RemainingArgs
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$pythonScript = Join-Path $scriptDir "embed_apk.py"

if ($RemainingArgs.Count -eq 0) {
    python $pythonScript
} else {
    python $pythonScript @RemainingArgs
}

