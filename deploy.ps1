<#
.SYNOPSIS
    Deploy frcastr to IIS - applies EF migrations, publishes the web app.

.DESCRIPTION
    Steps performed:
      1. Validates administrator privileges
      2. Resolves IIS destination path and app pool from the provided parameters
      3. Restores dotnet local tools (dotnet-ef)
      4. Reads the connection string from setup-generated.json at the destination
         (or use -ConnectionString to override)
      5. Builds and publishes the web project
      6. Stops the IIS app pool
      7. Applies any pending EF Core migrations
      8. Copies published files to the IIS site, preserving setup-generated.json
         and appsettings.Production.json
      9. Starts the IIS app pool (always, even on failure)

    Must be run as Administrator (required for IIS management).

.EXAMPLE
    .\deploy.ps1 -IISSiteName "frcastr"
    .\deploy.ps1 -IISSiteName "frcastr" -SkipMigrations
    .\deploy.ps1 -IISSiteName "frcastr" -ConnectionString "Server=.;Database=frcastr;..."
    .\deploy.ps1 -IISSiteUrl "https://weather.example.com"
#>

param(
    [string]$WebProject        = (Join-Path $PSScriptRoot 'src\frcastr.Web\frcastr.Web.csproj'),
    [string]$MigrationsProject = (Join-Path $PSScriptRoot 'src\frcastr.Infrastructure\frcastr.Infrastructure.csproj'),
    [string]$PublishDir        = (Join-Path $PSScriptRoot 'publish\frcastr.Web'),
    [string]$Configuration     = 'Release',
    [string]$DestinationPath   = 'E:\Sites\frcastr', # will be overridden if IIS site or URL is specified
    [string]$IISAppPoolName    = 'frcastr',
    [string]$IISSiteName       = 'frcastr',
    [string]$IISSiteUrl        = '',
    [string]$ConnectionString  = '',
    [switch]$SkipMigrations
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#region helpers

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Write-Ok([string]$Message) {
    Write-Host $Message -ForegroundColor Green
}

function Invoke-Cmd([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$Exe $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function Get-IISSiteByUrl([string]$SiteUrl) {
    try { $uri = [uri]$SiteUrl } catch { throw "Invalid IIS site URL: $SiteUrl" }
    foreach ($s in Get-Website) {
        foreach ($b in $s.Bindings.Collection) {
            $parts = $b.bindingInformation -split ':'
            if ($parts.Length -lt 3) { continue }
            if ([int]$parts[1] -eq $uri.Port -and $b.protocol -eq $uri.Scheme) {
                if ($parts[2] -eq $uri.Host -or $parts[2] -eq '*' -or [string]::IsNullOrWhiteSpace($parts[2])) {
                    return $s
                }
            }
        }
    }
    return $null
}

#endregion

# ── admin check ───────────────────────────────────────────────────────────────

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as Administrator (required for IIS management)."
}

# ── resolve IIS target ────────────────────────────────────────────────────────

Write-Step "Resolving IIS deployment target"
Import-Module WebAdministration -ErrorAction Stop

if (-not [string]::IsNullOrWhiteSpace($IISSiteUrl)) {
    $site = Get-IISSiteByUrl -SiteUrl $IISSiteUrl
    if (-not $site) { throw "No IIS site matches URL '$IISSiteUrl'." }
    $IISSiteName = $site.Name

    $urlPath = ([uri]$IISSiteUrl).AbsolutePath.TrimEnd('/')
    if (-not [string]::IsNullOrWhiteSpace($urlPath) -and $urlPath -ne '/') {
        $vApp = Get-WebApplication -Site $site.Name | Where-Object { $_.Path.TrimEnd('/') -eq $urlPath }
        if ($vApp) {
            $DestinationPath = [Environment]::ExpandEnvironmentVariables($vApp.PhysicalPath)
            Write-Host "Resolved virtual app '$urlPath' -> '$DestinationPath'."
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($IISSiteName)) {
    $site = Get-Website -Name $IISSiteName -ErrorAction Stop
    if (-not $site) { throw "IIS site '$IISSiteName' not found." }
    if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
        $DestinationPath = [Environment]::ExpandEnvironmentVariables($site.PhysicalPath)
    }
    if ([string]::IsNullOrWhiteSpace($IISAppPoolName)) {
        $IISAppPoolName = $site.ApplicationPool
    }
}

if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    throw "DestinationPath could not be determined. Provide -DestinationPath, -IISSiteName, or -IISSiteUrl."
}

$poolLabel = if ([string]::IsNullOrWhiteSpace($IISAppPoolName)) { '(not specified)' } else { $IISAppPoolName }
Write-Ok "Destination : $DestinationPath"
Write-Ok "App pool    : $poolLabel"

# ── restore dotnet tools ──────────────────────────────────────────────────────

Write-Step "Restoring dotnet local tools"
Invoke-Cmd 'dotnet' @('tool', 'restore')
Write-Ok "Tools ready."

# ── resolve connection string ─────────────────────────────────────────────────

$runMigrations = -not $SkipMigrations.IsPresent

if ($runMigrations -and [string]::IsNullOrWhiteSpace($ConnectionString)) {
    Write-Step "Reading connection string from deployed config"
    $configFile = Join-Path $DestinationPath 'setup-generated.json'
    if (Test-Path $configFile) {
        try {
            $ConnectionString = (Get-Content $configFile -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection
        } catch {
            Write-Host "Could not parse '$configFile': $_"
        }
    }

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        Write-Host "No connection string found at '$configFile'."
        Write-Host "Migrations will be skipped. Run the Setup wizard first, or pass -ConnectionString explicitly."
        $runMigrations = $false
    } else {
        Write-Ok "Connection string loaded from setup-generated.json."
    }
}

# ── build and publish ─────────────────────────────────────────────────────────

Write-Step "Building and publishing ($Configuration)"

if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}

Invoke-Cmd 'dotnet' @('publish', $WebProject, '-c', $Configuration, '-o', $PublishDir)
Write-Ok "Published to: $PublishDir"

# ── stop app pool ─────────────────────────────────────────────────────────────

$hasPool = -not [string]::IsNullOrWhiteSpace($IISAppPoolName)

if ($hasPool) {
    Write-Step "Stopping app pool '$IISAppPoolName'"
    $state = (Get-WebAppPoolState -Name $IISAppPoolName -ErrorAction Stop).Value
    if ($state -ne 'Stopped') {
        Stop-WebAppPool -Name $IISAppPoolName
        $elapsed = 0
        while ((Get-WebAppPoolState -Name $IISAppPoolName).Value -ne 'Stopped' -and $elapsed -lt 30) {
            Start-Sleep -Seconds 1
            $elapsed++
        }
        if ((Get-WebAppPoolState -Name $IISAppPoolName).Value -ne 'Stopped') {
            Write-Host "App pool did not stop within 30 s - continuing anyway."
        } else {
            Write-Ok "App pool stopped."
        }
    } else {
        Write-Host "App pool was already stopped."
    }
} else {
    Write-Host "No app pool specified - IIS pool will not be managed."
}

# ── migrate + copy (pool always restarted in finally) ────────────────────────

try {
    if ($runMigrations) {
        Write-Step "Applying EF Core migrations"
        Invoke-Cmd 'dotnet' @(
            'ef', 'database', 'update',
            '--project',         $MigrationsProject,
            '--startup-project', $WebProject,
            '--configuration',   $Configuration,
            '--no-build',
            '--connection',      $ConnectionString
        )
        Write-Ok "Migrations applied."
    } else {
        Write-Host "`nMigrations skipped."
    }

    Write-Step "Copying files to IIS site"

    if (-not (Test-Path $DestinationPath)) {
        New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
    }

    $rcArgs = @(
        $PublishDir, $DestinationPath,
        '/MIR',
        '/XF', 'setup-generated.json', 'appsettings.Production.json',
        '/XD', 'uploads',
        '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS'
    )
    robocopy @rcArgs
    if ($LASTEXITCODE -ge 8) { throw "Robocopy failed with exit code $LASTEXITCODE." }

    Write-Ok "Files deployed to $DestinationPath"
} finally {
    if ($hasPool) {
        Write-Step "Starting app pool '$IISAppPoolName'"
        Start-WebAppPool -Name $IISAppPoolName
        Write-Ok "App pool started."
    }
}

Write-Host "`nDeployment complete!" -ForegroundColor Green
