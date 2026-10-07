param(
    [string]$Version = "0.1.0",
    [string]$InnoCompiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version: oczekiwany format 0.1.0" }
function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Polecenie zakończyło się błędem: $Executable" }
}
Invoke-Checked dotnet @("restore", "KsgMail.sln")
Invoke-Checked dotnet @("test", "KsgMail.sln", "-c", "Release", "--no-restore")
Invoke-Checked dotnet @("publish", "src/KsgMail.App/KsgMail.App.csproj", "-c", "Release",
    "-r", "win-x64", "--self-contained", "true", "-o", "artifacts/publish",
    "-p:Version=$Version", "-p:PublishSingleFile=false")
if (!(Test-Path -LiteralPath $InnoCompiler)) { throw "Zainstaluj Inno Setup 6 lub podaj -InnoCompiler." }
Invoke-Checked $InnoCompiler @("/DAppVersion=$Version", "installer/ksg-mail.iss")
Compress-Archive -Path artifacts/publish/* -DestinationPath "artifacts/KsgMail-$Version-win-x64.zip" -Force
Get-ChildItem artifacts/installer/*.exe, artifacts/*.zip | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content -Encoding ascii artifacts/SHA256SUMS.txt
