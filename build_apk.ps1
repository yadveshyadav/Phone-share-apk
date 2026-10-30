$ErrorActionPreference = "Stop"

$javaHome = "C:\Program Files\Android\Android Studio\jbr"
$env:JAVA_HOME = $javaHome
$javaBin = Join-Path $javaHome "bin"
$env:PATH = "$javaBin;$env:PATH"
$javac = Join-Path $javaBin "javac.exe"
$java = Join-Path $javaBin "java.exe"
$androidJar = "C:\Users\Lokendra\AppData\Local\Android\Sdk\platforms\android-36.1\android.jar"
$buildTools = "C:\Users\Lokendra\AppData\Local\Android\Sdk\build-tools\36.0.0"
$d8 = Join-Path $buildTools "d8.bat"
$zipalign = Join-Path $buildTools "zipalign.exe"
$apksigner = Join-Path $buildTools "apksigner.bat"
$keystore = "C:\Users\Lokendra\.android\debug.keystore"
$apktoolJar = "C:\Users\Lokendra\AppData\Local\Temp\apktool.jar"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$tempDir = "C:\Users\Lokendra\AppData\Local\Temp"

$repoSrcDir = Join-Path $scriptDir "AndroidApp\java_src"
$srcDir = if (Test-Path $repoSrcDir) { $repoSrcDir } else { Join-Path $tempDir "java_src" }
$outClasses = Join-Path $tempDir "java_bin"
$d8Out = Join-Path $tempDir "d8_calc_out"
$calcZip = Join-Path $tempDir "calc_helper.zip"
$calcSmali = Join-Path $tempDir "calc_smali"
$apkWithSmali = Join-Path $tempDir "apk_with_smali"

$unsignedApk = Join-Path $tempDir "calculator_full_unsigned.apk"
$alignedApk = Join-Path $tempDir "calculator_full_aligned.apk"
$signedApk = Join-Path $tempDir "calculator_full_signed.apk"

Write-Host "=== 1. Compiling Java Sources ===" -ForegroundColor Cyan
if (!(Test-Path $outClasses)) { New-Item -ItemType Directory -Path $outClasses -Force | Out-Null }
$javaSources = Get-ChildItem -Path $srcDir -Recurse -Filter "*.java" | ForEach-Object { $_.FullName }
& $javac -cp $androidJar -d $outClasses @javaSources
if ($LASTEXITCODE -ne 0) { throw "javac compilation failed." }

Write-Host "=== 2. Converting Classes to DEX with D8 ===" -ForegroundColor Cyan
if (Test-Path $d8Out) { Remove-Item -Recurse -Force $d8Out }
New-Item -ItemType Directory -Path $d8Out -Force | Out-Null
$classes = Get-ChildItem -Path (Join-Path $outClasses "com\companyname\consentcapture\android") -Recurse -Filter "*.class" | ForEach-Object { $_.FullName }
& $d8 --output $d8Out --lib $androidJar @classes
if ($LASTEXITCODE -ne 0) { throw "d8 dex conversion failed." }

Write-Host "=== 3. Packaging DEX and Disassembling to Smali ===" -ForegroundColor Cyan
if (Test-Path $calcZip) { Remove-Item -Force $calcZip }
Compress-Archive -Path (Join-Path $d8Out "classes.dex") -DestinationPath $calcZip -Force
if (Test-Path $calcSmali) { Remove-Item -Recurse -Force $calcSmali }
& $java -jar $apktoolJar d $calcZip -o $calcSmali -f
if ($LASTEXITCODE -ne 0) { throw "apktool disassembly of classes.dex failed." }

Write-Host "=== 4. Updating Smali in APK Directory ===" -ForegroundColor Cyan
$targetSmaliDir = Join-Path $apkWithSmali "smali\com\companyname\consentcapture\android"
Get-ChildItem -Path (Join-Path $calcSmali "smali\com\companyname\consentcapture\android") -Filter "*.smali" | ForEach-Object {
    Copy-Item $_.FullName -Destination $targetSmaliDir -Force
    Write-Host "  Updated smali: $($_.Name)" -ForegroundColor Green
}

Write-Host "=== 5. Building Unsigned APK with Apktool ===" -ForegroundColor Cyan
if (Test-Path $unsignedApk) { Remove-Item -Force $unsignedApk }
& $java -jar $apktoolJar b $apkWithSmali -o $unsignedApk
if ($LASTEXITCODE -ne 0) { throw "apktool build failed." }

Write-Host "=== 6. Aligning APK with zipalign ===" -ForegroundColor Cyan
if (Test-Path $alignedApk) { Remove-Item -Force $alignedApk }
& $zipalign -p -f -v 4 $unsignedApk $alignedApk
if ($LASTEXITCODE -ne 0) { throw "zipalign failed." }

Write-Host "=== 7. Signing APK with apksigner ===" -ForegroundColor Cyan
if (Test-Path $signedApk) { Remove-Item -Force $signedApk }
if (Test-Path "$signedApk.idsig") { Remove-Item -Force "$signedApk.idsig" }
& $apksigner sign --ks $keystore --ks-pass "pass:android" --ks-key-alias "androiddebugkey" --key-pass "pass:android" --out $signedApk $alignedApk
if ($LASTEXITCODE -ne 0) { throw "apksigner failed." }

Write-Host "=== 8. Deploying APK to Output Locations ===" -ForegroundColor Cyan
$destinations = @(
    "C:\Users\Lokendra\Downloads\ConsentCapture\ConsentCapture\Calculator.apk",
    "C:\Users\Lokendra\Downloads\ConsentCapture\ConsentCapture\ConsentCapture-Android.apk",
    "C:\Users\Lokendra\Downloads\ConsentCapture\ConsentCapture\AndroidApp\bin\Release\net10.0-android\com.companyname.ConsentCapture.Android-Signed.apk",
    "C:\Users\Lokendra\Downloads\ConsentCapture\Calculator.apk"
)

foreach ($dest in $destinations) {
    $parent = Split-Path $dest -Parent
    if (!(Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item -Path $signedApk -Destination $dest -Force
    Write-Host "  Deployed to: $dest" -ForegroundColor Green
}

Write-Host "`n[SUCCESS] APK build complete!" -ForegroundColor Green
