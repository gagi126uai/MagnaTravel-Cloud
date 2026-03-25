param(
    [Parameter(Mandatory = $true)]
    [string]$BackupFile,
    [string]$ContainerName = "travel_db",
    [string]$ShadowDatabase = "travel_shadow",
    [string]$Username = $env:POSTGRES_USER
)

if ([string]::IsNullOrWhiteSpace($Username)) {
    $Username = "traveluser"
}

if (-not (Test-Path $BackupFile)) {
    throw "Backup file not found: $BackupFile"
}

Write-Host "Recreating shadow database $ShadowDatabase in container $ContainerName..."
docker exec $ContainerName psql -U $Username -d postgres -c "DROP DATABASE IF EXISTS $ShadowDatabase;"
docker exec $ContainerName psql -U $Username -d postgres -c "CREATE DATABASE $ShadowDatabase;"

Write-Host "Restoring $BackupFile into $ShadowDatabase..."
Get-Content -Encoding Byte -Path $BackupFile | docker exec -i $ContainerName pg_restore -U $Username -d $ShadowDatabase --clean --if-exists

Write-Host "Restore completed."
