# Changelog

All notable changes to frcastr are documented here.

## [Unreleased]

### Added
- Multiple named dashboards: widgets belong to a named dashboard; navigate between dashboards with the `?dash=<name>` query parameter. Layouts are persisted per (user, dashboard name) pair. The admin Widgets page exposes a Dashboard field on every widget.
- Data sources now have a dedicated **URL** field. Enter the endpoint URL directly in the admin UI instead of embedding it in the Config JSON blob.
- Forecast sources with a URL automatically route to the `generic-json` adapter (returns a `ForecastPeriod[]` array) without requiring `"provider": "generic-json"` in Config.
- Pull sources with a URL automatically route to the `generic` HTTP adapter; channel mapping can still be supplied via Config JSON.
- DataSink (generic HTTP sink) sources use `DataSource.Url` over any `url` key in Config JSON.
- All generic HTTP adapters (`GenericHttpAdapter`, `GenericJsonForecastAdapter`, `GenericHttpSinkAdapter`) prefer `DataSource.Url` and fall back to the config `url` property for backwards compatibility with existing sources.

## [0.1.1] — 2026-06-21

### Fixed
- Dashboard widgets displayed raw HTML text instead of rendered content: `grid.addWidget` no longer relies on GridStack's `content` option (which was treating the string as text); the shell HTML is now injected directly into `.grid-stack-item-content` after the item is created.
- `GET /api/dashboard/widgets` now returns an explicit projection with `type` cast to `int`, ensuring the JSON value is always a number regardless of the JSON serializer's enum-handling configuration.
- Added `.config/dotnet-tools.json` manifest so `dotnet tool restore` in `deploy.ps1` succeeds instead of failing with "Cannot find a manifest file".

## [0.1.0] — 2026-06-21

### Added
- Initial release: complete weather station web application
- Three-project layered architecture: `frcastr.Core`, `frcastr.Infrastructure`, `frcastr.Web`
- ASP.NET Core 10 + EF Core 10 + MSSQL with automated migrations
- ASP.NET Core Identity with custom RBAC (`Permission` entity, resource/action matrix)
- Six-step setup wizard (Database, Admin, Station, Email, Branding, Review)
- Push ingest API (`POST /api/ingest`, `POST /api/ingest/batch`) with SHA-256 API key auth and rate limiting
- Tiered data storage: raw readings → hourly aggregates → daily aggregates → all-time channel records
- Automatic aggregation background services (hourly + daily) with configurable retention
- GridStack v12 dashboard (12-column desktop, 2-column mobile) with 18 widget types
- Live data poll loop (default 30 s) with stale-channel detection and `⚠ stale` badges
- Calculated virtual channels: feels-like, wind chill, heat index, dew point
- Weather animations (CSS-only): sun, cloud, rain, lightning, snow
- Chart.js sparklines for pressure trend widget with gap detection
- Dark/light/auto theme with `prefers-color-scheme` and localStorage persistence
- Dashboard layout persistence (server for authenticated users, localStorage for anonymous)
- Kiosk mode: hides nav, disables drag/resize, auto-refreshes every hour
- Forecast data: NWS (free), OpenWeatherMap (API key), weatherapi.com (API key), generic JSON proxy
- NWS severe weather alerts with severity color-coding and email notification
- Sunrise/sunset and moon phase calculators (pure math, no API)
- Air quality via OpenWeatherMap Air Pollution API or push ingest
- MQTT data source support (MQTTnet, configurable topic→channel mapping, calibration offsets)
- Outbound data upload to Weather Underground, PWSWeather, or custom HTTP endpoint
- Generic HTTP pull adapter (JSON path mapping) and generic JSON forecast proxy adapter
- Webhook threshold alerts with cooldown and optional email notification
- Email alerts: sensor offline, severe weather, digest, webhook trigger
- Admin panel: data sources (with API key rotation), widgets, users, roles, webhooks, audit log
- Settings page with nine accordion sections
- History page: period summary, all-time records, CSV export
- OpenAPI + Scalar UI (admin-only in production)
- Health check endpoint (`/health`)
- PWA manifest with SVG icons
- IIS deploy script (`deploy.ps1`) with migration, robocopy, and app pool management
