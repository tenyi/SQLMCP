param([string]$Destination)
$ErrorActionPreference = 'Stop'
if (-not $Destination) {
    $profileRoot = if ($env:HOME) { $env:HOME } else { [Environment]::GetFolderPath('UserProfile') }
    $Destination = Join-Path $profileRoot '.local\bin'
}
$projectPath = Join-Path $PSScriptRoot '..\src\SqlMcp\SqlMcp.csproj'
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:AssemblyName=sqlmcp -o $Destination
if ($LASTEXITCODE -ne 0) { throw '發佈失敗' }
Write-Host "已安裝至 $Destination\sqlmcp.exe"
