# בונה את התוכנה ואורז אותה לתיקייה ולקובץ zip בתיקייה dist.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
dotnet build "$root\src\SefarimEditor\SefarimEditor.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "הבנייה נכשלה" }
$bin = "$root\src\SefarimEditor\bin\Release\net48\win-x64"
$out = "$root\dist\עורך הספרייה"
if (Test-Path $out) { Get-ChildItem $out -Recurse | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
# רק מה שנדרש להרצה: בלי קובצי ניפוי באגים ובלי ספריות לארכיטקטורות אחרות.
# כל הספריות ארוזות בתוך ה-EXE, ולכן מספיק הקובץ הזה בלבד.
Copy-Item "$bin\SefarimEditor.exe" $out
Copy-Item "$root\dist-files\*" $out
$zip = "$root\dist\עורך הספרייה.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip
"נוצר: $zip"

# חבילה מלאה: התוכנה + רכיב החיפוש לזית בתיקייה addons (אם נבנה קודם ב-addon\build.ps1).
$addon = Get-ChildItem "$root\addon\out" -Directory -Filter "SefarimSearch-*" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($addon) {
    $full = "$root\dist\עורך הספרייה - מלא"
    if (Test-Path $full) { Remove-Item $full -Recurse -Force }
    New-Item -ItemType Directory -Force "$full\עורך הספרייה\addons" | Out-Null
    Copy-Item "$out\*" "$full\עורך הספרייה"
    Copy-Item $addon.FullName "$full\עורך הספרייה\addons" -Recurse
    $fullZip = "$root\dist\עורך הספרייה - מלא.zip"
    if (Test-Path $fullZip) { Remove-Item $fullZip -Force }
    # ב-PowerShell 5.1 גם Compress-Archive וגם ZipFile כותבים '\' בשמות שבתוך ה-zip; לכן בונים את הרשומות ידנית עם '/'.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $fs = [IO.File]::Create($fullZip)
    $archive = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($f in Get-ChildItem $full -Recurse -File) {
        $rel = $f.FullName.Substring($full.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $archive.Dispose()
    $fs.Dispose()
    Remove-Item $full -Recurse -Force
    "נוצר: $fullZip"
}