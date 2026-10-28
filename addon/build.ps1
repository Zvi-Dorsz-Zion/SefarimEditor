# בונה את רכיב החיפוש לזית: קובץ jar + סביבת הרצה מינימלית של ג'אווה (jlink), ואורז ל-zip.
# דרישות: JDK 21 ומעלה (‎-Jdk), וקובצי Lucene 10.5.2 מ-Maven Central (‎-Lucene: תיקייה עם lucene-core ו-lucene-analysis-common).
# הפלט: addon\out\SefarimSearch-<גרסה>.zip, וגודלו וה-SHA-256 שלו (להעתיק ל-SearchAddon.cs).
param(
    [Parameter(Mandatory = $true)][string]$Jdk,
    [Parameter(Mandatory = $true)][string]$Lucene
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = "1.0.0"
$luceneVersion = "10.5.2"
$name = "SefarimSearch-$version"
$out = Join-Path $root "out"
$classes = Join-Path $out "classes"
$stage = Join-Path $out $name
$jars = @("lucene-core-$luceneVersion.jar", "lucene-analysis-common-$luceneVersion.jar")

foreach ($j in $jars) { if (-not (Test-Path (Join-Path $Lucene $j))) { throw "חסר $j" } }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $classes, (Join-Path $stage "lib") | Out-Null

$cp = ($jars | ForEach-Object { Join-Path $Lucene $_ }) -join ";"
& "$Jdk\bin\javac.exe" --release 21 -encoding UTF-8 -cp $cp -d $classes (Join-Path $root "src\SefarimSearch.java")
if ($LASTEXITCODE -ne 0) { throw "javac נכשל" }

$manifest = Join-Path $out "manifest.txt"
$classPath = ($jars | ForEach-Object { "lib/$_" }) -join " "
[IO.File]::WriteAllText($manifest, "Main-Class: SefarimSearch`r`nClass-Path: $classPath`r`n", (New-Object Text.UTF8Encoding($false)))
& "$Jdk\bin\jar.exe" --create --file (Join-Path $stage "sefarim-search.jar") --manifest $manifest -C $classes .
if ($LASTEXITCODE -ne 0) { throw "jar נכשל" }
foreach ($j in $jars) { Copy-Item (Join-Path $Lucene $j) (Join-Path $stage "lib") }

# המודולים שהקוד ו-Lucene צריכים (jdeps), ועוד jdk.unsupported ש-Lucene משתמשת בו דרך השתקפות.
$mods = & "$Jdk\bin\jdeps.exe" --print-module-deps --ignore-missing-deps --multi-release 21 -cp $cp (Join-Path $stage "sefarim-search.jar")
if ($LASTEXITCODE -ne 0) { throw "jdeps נכשל" }
$mods = (($mods -split ",") + "jdk.unsupported" | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique) -join ","
"מודולים: $mods"
& "$Jdk\bin\jlink.exe" --add-modules $mods --strip-debug --no-header-files --no-man-pages --compress zip-6 --output (Join-Path $stage "runtime")
if ($LASTEXITCODE -ne 0) { throw "jlink נכשל" }

Copy-Item (Join-Path $root "README.txt") $stage
[IO.File]::WriteAllText((Join-Path $stage "sefarim-search.ok"), $version, (New-Object Text.UTF8Encoding($false)))

$check = & (Join-Path $stage "runtime\bin\java.exe") -jar (Join-Path $stage "sefarim-search.jar") version
if ($LASTEXITCODE -ne 0 -or -not ("$check" -like "sefarim-search $version*")) { throw "הבדיקה של הרכיב נכשלה: $check" }
"נבדק: $check"

$zip = Join-Path $out "$name.zip"
# ב-PowerShell 5.1 גם Compress-Archive וגם ZipFile כותבים '\' בשמות שבתוך ה-zip, בניגוד לתקן; לכן בונים את הרשומות ידנית עם '/'.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$fs = [IO.File]::Create($zip)
$archive = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
foreach ($f in Get-ChildItem $stage -Recurse -File) {
    $rel = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
$archive.Dispose()
$fs.Dispose()
$size = (Get-Item $zip).Length
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"נוצר: $zip"
"ZipSize = $size"
"ZipSha256 = $sha"
