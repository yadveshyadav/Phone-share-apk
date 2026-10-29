<#
.SYNOPSIS
    PowerShell launcher for install_app.py
.DESCRIPTION
    Universal Android App Installer and File Provider.
    Supports Wi-Fi QR Code installer, ADB direct USB install, carrier extraction, and export.
.EXAMPLE
    .\install_app.ps1
    .\install_app.ps1 "app.apk"
    .\install_app.ps1 "carrier.jpg"
    .\install_app.ps1 --wifi
    .\install_app.ps1 --adb
    .\install_app.ps1 -f "my_carrier.jpg" --wifi
    .\install_app.ps1 --export
#>
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$ArgsList
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$pythonScript = Join-Path $scriptDir "install_app.py"

if (-not $ArgsList -or $ArgsList.Count -eq 0) {
    python $pythonScript
} elseif ($ArgsList.Count -eq 1 -and (Test-Path $ArgsList[0])) {
    python $pythonScript -f $ArgsList[0]
} else {
    python $pythonScript @ArgsList
}
