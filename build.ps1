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