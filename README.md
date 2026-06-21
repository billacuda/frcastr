# frcastr
![Project Logo](images/frcastr_logo_glass_tile.svg)

Personal weather station web application. Accepts pushed sensor data and pulls from external providers, displays live readings on a customizable dashboard, and archives everything to SQL Server.

## **Current version [0.1.1](CHANGELOG.md)**

## Stack

- ASP.NET Core 10, Razor Pages, MVC API controllers
- EF Core 10 + SQL Server
- Bootstrap 5, GridStack v12, Chart.js (CDN)
- IIS InProcess hosting

## Quick start

1. Create an IIS site pointing at an empty folder (e.g. `E:\Sites\frcastr`)
2. Run the deploy script (must be Administrator):

```powershell
.\deploy.ps1 -IISSiteName "frcastr"
```

3. Browse to the site — the setup wizard will open automatically

## Deploy script

```powershell
# By IIS site name (reads connection string from setup-generated.json at the site root)
.\deploy.ps1 -IISSiteName "frcastr"

# By URL
.\deploy.ps1 -IISSiteUrl "https://weather.example.com"

# Skip migrations (e.g. first run before the wizard has created setup-generated.json)
.\deploy.ps1 -IISSiteName "frcastr" -SkipMigrations

# Provide connection string explicitly
.\deploy.ps1 -IISSiteName "frcastr" -ConnectionString "Server=.;Database=frcastr;..."
```

## Data sources

| Type | Description |
|---|---|
| Push | HTTP `POST /api/ingest` with `X-Api-Key` header |
| Pull | Periodic HTTP fetch (OpenWeatherMap, generic JSON) |
| Forecast | NWS, OpenWeatherMap, weatherapi.com, generic JSON proxy |
| MQTT | Subscribe to broker topics with topic→channel mapping |
| DataSink | Upload to Weather Underground, PWSWeather, or custom endpoint |
| Alerts | NWS severe weather alerts |
| AirQuality | OpenWeatherMap Air Pollution API or push ingest |

## Dashboard widgets

DateTime, Temperature (outdoor/indoor), Humidity (outdoor/indoor), Pressure, Wind, Weather Animation, Forecast, Moon Phase, Sunrise/Sunset, Alerts, Feels Like, Rainfall, Pressure Trend, Air Quality, Radar, Custom channel.

## Version history

See [CHANGELOG.md](CHANGELOG.md).
