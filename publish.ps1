<#
  Builds DochkaDock as a small, framework-dependent single .exe
  (~200 KB). Requires the .NET 8 Desktop Runtime on the machine it runs on
  (a one-time, shared install — the .exe prompts with a download link if
  it's missing). See the trimming/deployment note in DochkaDock.csproj
  if you need a self-contained build instead (no runtime install, but ~70 MB).
  Output: .\publish\DochkaDock.exe
#>
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

dotnet publish "$root\src\DochkaDock\DochkaDock.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o "$root\publish"

Write-Host ""
Write-Host "Published to $root\publish\DochkaDock.exe" -ForegroundColor Green
Get-ChildItem "$root\publish" | Format-Table Name, Length -AutoSize
