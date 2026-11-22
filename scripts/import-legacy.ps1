<#
.SYNOPSIS
  Imports a backup of the old PHP decks-api database into the DeckApi database.

.DESCRIPTION
  1. Starts a throwaway PostgreSQL 17 container and restores the backup into it
     (plain .sql / .sql.gz through psql, custom/tar/directory dumps through pg_restore).
  2. Runs `DeckApi import-legacy` against it: users, decks (re-encoded + revalidated) and upvotes are
     upserted into the target database. Re-runnable: decks already imported are only updated when the
     backup is newer.
  3. Removes the container.

  Requires Docker and the .NET 10 SDK. The target database schema is migrated first (--migrate).
  The card catalog comes from the target DB snapshot, or from uniques-search-api (UniquesSearch__BaseUrl).

.EXAMPLE
  ./scripts/import-legacy.ps1 -Backup ./decks-2026-11-20.dump `
      -Target "Host=localhost;Database=deckapi;Username=postgres;Password=postgres" `
      -UniquesSearchUrl "http://localhost:8234"
#>
param(
    [Parameter(Mandatory)] [string] $Backup,
    [Parameter(Mandatory)] [string] $Target,
    [string] $UniquesSearchUrl = $env:UniquesSearch__BaseUrl,
    [switch] $DryRun,
    [int] $Port = 55432
)

$ErrorActionPreference = 'Stop'
$Backup = (Resolve-Path $Backup).Path
$container = "deckapi-legacy-import-$PID"
$password = 'import'

try {
    docker run -d --name $container -e POSTGRES_PASSWORD=$password -e POSTGRES_DB=legacy -p "${Port}:5432" postgres:17-alpine | Out-Null
    Write-Host "Waiting for PostgreSQL..."
    for ($i = 0; $i -lt 60; $i++) {
        # TCP check: during initdb the temporary server only listens on the unix socket.
        docker exec $container pg_isready -h 127.0.0.1 -U postgres -d legacy 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 1
    }

    docker cp $Backup "${container}:/tmp/backup"
    Write-Host "Restoring $Backup..."
    if ($Backup -match '\.sql\.gz$') {
        docker exec $container sh -c "gunzip -c /tmp/backup | psql -q -v ON_ERROR_STOP=0 -U postgres -d legacy" | Out-Null
    } elseif ($Backup -match '\.sql$') {
        docker exec $container psql -q -v ON_ERROR_STOP=0 -U postgres -d legacy -f /tmp/backup | Out-Null
    } else {
        # Ownership/privileges of the production roles don't exist here: ignore them.
        docker exec $container pg_restore --no-owner --no-privileges -U postgres -d legacy /tmp/backup
    }
    if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE" }

    $source = "Host=localhost;Port=$Port;Database=legacy;Username=postgres;Password=$password"
    $env:ConnectionStrings__decks = $Target
    if ($UniquesSearchUrl) { $env:UniquesSearch__BaseUrl = $UniquesSearchUrl }
    elseif (-not $env:UniquesSearch__BaseUrl) { $env:UniquesSearch__BaseUrl = 'http://127.0.0.1:9' } # snapshot only

    $importArgs = @('import-legacy', '--source', $source, '--migrate')
    if ($DryRun) { $importArgs += '--dry-run' }
    $project = Join-Path $PSScriptRoot '..\src\DeckApi\DeckApi.csproj'
    dotnet run --project $project -c Release -- @importArgs
    if ($LASTEXITCODE -ne 0) { throw "Import failed with exit code $LASTEXITCODE" }
}
finally {
    docker rm -f $container 2>$null | Out-Null
}
