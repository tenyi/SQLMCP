param([string]$Destination)
$ErrorActionPreference = 'Stop'
if (-not $Destination) {
    $profileRoot = if ($env:HOME) { $env:HOME } else { [Environment]::GetFolderPath('UserProfile') }
    $Destination = Join-Path $profileRoot '.local\bin'
}
$projectPath = Join-Path $PSScriptRoot '..\src\SqlMcp\SqlMcp.csproj'
$exePath = Join-Path $Destination 'sqlmcp.exe'

# 清除先前安裝留下的舊版檔案；仍被執行中的 session 鎖住的就先跳過，等下次安裝再刪。
if (Test-Path $Destination) {
    Get-ChildItem -Path $Destination -Filter 'sqlmcp.exe.*.old' | ForEach-Object {
        try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop } catch { }
    }
}

# 執行中的 exe 在 Windows 上無法覆寫，但可以改名。
# 先把現有版本改成唯一的 .old 名稱（帶時間戳，避免與仍被鎖住的舊 .old 衝突），
# 讓 publish 能寫入新的 sqlmcp.exe；已在執行的 session 繼續使用舊檔，新開的 session 才會用新版。
$backupPath = $null
if (Test-Path $exePath) {
    $backupPath = Join-Path $Destination ("sqlmcp.exe.{0}.old" -f (Get-Date -Format 'yyyyMMddHHmmssfff'))
    Rename-Item -LiteralPath $exePath -NewName (Split-Path $backupPath -Leaf)
}

dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:AssemblyName=sqlmcp -o $Destination
if ($LASTEXITCODE -ne 0) {
    # 發佈失敗且沒有產生新的 exe 時，把舊版改回原名，避免安裝後反而沒有可用的 sqlmcp.exe。
    if ($backupPath -and -not (Test-Path $exePath)) {
        Rename-Item -LiteralPath $backupPath -NewName 'sqlmcp.exe'
    }
    throw '發佈失敗'
}

# 舊版若沒有被任何程序使用，現在就刪掉；被鎖住則保留，下次安裝時清除。
if ($backupPath) {
    try { Remove-Item -LiteralPath $backupPath -Force -ErrorAction Stop } catch { }
}
Write-Host "已安裝至 $exePath（已執行中的 session 需重新啟動才會使用新版）"
