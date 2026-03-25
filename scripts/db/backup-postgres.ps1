param(
    [string]$ContainerName = "travel_db",
    [string]$Database = $env:POSTGRES_DB,
    [string]$Username = $env:POSTGRES_USER,
    [string]$BackupRoot = ".\\backups\\postgres",
    [int]$DailyRetentionDays = 14,
    [int]$WeeklyRetentionWeeks = 8
)

if ([string]::IsNullOrWhiteSpace($Database)) {
    $Database = "travel"
}

if ([string]::IsNullOrWhiteSpace($Username)) {
    $Username = "traveluser"
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$dailyDir = Join-Path $BackupRoot "daily"
$weeklyDir = Join-Path $BackupRoot "weekly"
$dailyFile = Join-Path $dailyDir "$Database-$timestamp.dump"

New-Item -ItemType Directory -Force -Path $dailyDir | Out-Null
New-Item -ItemType Directory -Force -Path $weeklyDir | Out-Null

Write-Host "Creating backup $dailyFile from container $ContainerName..."
docker exec $ContainerName pg_dump -Fc -U $Username $Database | Set-Content -Encoding Byte -Path $dailyFile

if ((Get-Date).DayOfWeek -eq [System.DayOfWeek]::Sunday) {
    $weeklyFile = Join-Path $weeklyDir "$Database-$timestamp.dump"
    Copy-Item $dailyFile $weeklyFile -Force
}

Get-ChildItem $dailyDir -File | Where-Object {
    $_.LastWriteTime -lt (Get-Date).AddDays(-$DailyRetentionDays)
} | Remove-Item -Force

Get-ChildItem $weeklyDir -File | Where-Object {
    $_.LastWriteTime -lt (Get-Date).AddDays(-7 * $WeeklyRetentionWeeks)
} | Remove-Item -Force

Write-Host "Backup finished."
