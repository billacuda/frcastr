# Changelog

All notable changes to frcastr are documented here.

## [Unreleased]

### Added
- **Multi-device MQTT ingestion**: a single MQTT data source can now serve any number of sensor devices. A new `topicPattern` config key (e.g. `frcastr/{device}/state`) extracts a device id from the topic, and the subscribe filter is derived from it automatically when `topic` is omitted, so the subscription and the matcher cannot drift apart. Previously `channelMapping` was an exact-topic dictionary lookup, so wildcard subscriptions connected fine but every message was silently discarded unless its full literal topic was a key in the map — meaning each device needed its own data source row and hand-enumerated topics.
- **`Device` entity**: devices auto-register on their first message (disable with `"autoRegisterDevices": false`) and carry a friendly name, location, model, firmware version, last-seen timestamp and per-device offline threshold. `WeatherReading`, `WeatherReadingAggregate` and `WeatherChannelRecord` gained a nullable `DeviceId`, so readings from non-device sources (NWS, OpenWeatherMap, Open-Meteo) are unaffected.
- **Per-device channel keys**: readings are addressed as `temperature.indoor@greenhouse-01`, with channel names themselves staying canonical so sanity bounds and calculated channels (dewpoint, feels-like, wind chill, heat index) apply per device. `GetCurrentReadingsAsync` now groups by `(ChannelName, DeviceId)`; grouping by channel alone meant two devices reporting the same channel overwrote each other on the dashboard and shared one all-time record row.
- **Primary device flag**: the primary device's readings are also published under the bare canonical channel name, so widgets bound to `temperature.outdoor` keep working when the station's own sensor becomes a device.
- **JSON MQTT payloads**: a message may now carry several measurements (`{"temperature":21.4,"humidity":55.2}`) mapped to channels via a new `fieldMapping` config key. Bare-decimal payloads and the legacy `channelMapping`/`channelUnits` keys still work unchanged. Optional `deviceId` and `firmware` payload fields are consumed as device metadata.
- **Admin → Devices page**: lists every registered device with last-seen status, and supports renaming, setting location/model, per-device offline thresholds, enable/disable, choosing the primary device, and deletion. Reachable from the admin dashboard cards and quick links.
- **Device-level offline alerts**: `SensorOfflineBackgroundService` now also emails when a whole device stops reporting or publishes an `offline` last will. This check reads `Device.LastSeenAt` from the database, so unlike the in-memory per-channel check it still fires after an app restart.
- **ESP32-S3 reference firmware** under `esp32/frcastr-sensor/`: a PlatformIO sketch (WiFi + MQTT with last will + SHT3x or DHT22) that implements the device contract. Flash the same sketch to every board, changing only `DEVICE_ID`.
- **`esp32-mqtt` data source preset** that pre-fills the multi-device MQTT config template.
- **Prefix sanity bounds**: channels outside the canonical set now fall back to family bounds (`temperature.*`, `humidity.*`, `dewpoint.*`, `pressure.*`, `battery.*`) instead of being completely unvalidated, plus a `battery.voltage` default.

### Fixed
- **New and edited MQTT sources needed an app restart**: `MqttBackgroundService` only re-scanned for sources when it held zero clients (`if (_clients.Count == 0)`), and failed clients were never removed from the list, so once any one source connected no source added afterwards was ever picked up. The service now reconciles its live clients against the database every 30 seconds — connecting new sources, dropping removed or disabled ones, and reconnecting those whose config changed.
- **MQTT config edits were ignored until restart**: the channel mapping was captured in the message-handler closure at connect time, so changes saved in Admin had no effect on a running connection. Config is now read from the reconciled source entry on each message.
- **MQTT readings misparsed on comma-decimal servers**: `decimal.TryParse` used the current culture, so a sensor publishing `21.4` was read as `214` where the server locale uses a comma decimal separator. Parsing is now invariant.
- **MQTT clients were never disconnected on shutdown**: the cleanup loop sat after a `while (await timer.WaitForNextTickAsync(stoppingToken))` that throws on stop rather than returning false, so it was unreachable. It now runs in a `finally`.
- **MQTT readings could sit up to 15 s stale on the dashboard**: the ingestion path never evicted the `weather-current` output cache tag, unlike the HTTP ingest endpoint. It now does, via a new `IWeatherCacheInvalidator` abstraction that keeps the Infrastructure project free of an ASP.NET Core dependency.
- **MQTT sources had no client id**, so broker logs showed only random GUIDs. Each connection now uses `frcastr-<sourceId>-<random>`.
- **Admin Test button understated MQTT config problems**: it used its own copy of the config model that ignored `channelMapping` and `channelUnits`. Both paths now share one `MqttSourceConfig`, and the test result reports the filter it subscribed to, whether each message matched `topicPattern`, the device ids seen, and the channels each payload resolved to.

## [0.4.2] - 2026-07-07

### Fixed
- **Alerts refresh crashed every tick, silently blocking severe-alert emails**: `AlertsRefreshBackgroundService` hardcoded `AlertCache.SourceId = 0` with a comment claiming "0 = NWS system source," but no `DataSources` row with `Id = 0` was ever seeded (`Id` is an identity column starting at 1), so every save threw `FK_AlertCaches_DataSources_SourceId` and aborted before the severe/extreme alert email step ever ran. The service now finds or creates a real "NWS Alerts" `DataSource` row (`Type=Alerts`, matching the existing admin-configurable source type used by `DataSourceTestService`) and uses its actual `Id`, so the source is also visible/manageable from Admin → Data Sources.

## [0.4.1] - 2026-07-02

### Fixed
- **Weather animation widget shows the moon during daytime**: `SunriseSunsetService` anchored its CoordinateSharp calculation on the UTC calendar date instead of the station's local calendar date. CoordinateSharp binds each sunrise/sunset/dawn/dusk/moonrise/moonset event to the UTC calendar day of the date it's given, so for stations far from UTC a single local day's morning and evening events can straddle two different UTC days — pairing "today's" sunrise with yesterday's or tomorrow's sunset. This made `isNightNow()` think it was night for several hours before sunset (west of UTC) or after sunrise (east of UTC). The service now anchors on the station's local date and scans the neighboring UTC days, keeping whichever result actually converts back to that local date.

## [0.4.0] - 2026-06-30

### Added
- **Moon-phase night animation for Weather Animation widget**: the `wa-sun` and `wa-partly-cloudy` animations now show the current moon-phase emoji (from `/api/weather/moon`) instead of the sun disc whenever the current time is before sunrise or after sunset (per `/api/weather/sun`), and switch back to the sun automatically at sunrise. Cloud/rain/snow/lightning animations are unchanged since they never rendered a sun/moon disc.

### Changed
- **Weather animation cloud thresholds adjusted**: `wa-cloud` now triggers above 65 % cloud coverage (was ≥ 50 %); `wa-partly-cloudy` now covers 20–64 % (was 20–49 %).

## [0.3.0] - 2026-06-27

### Added
- **Open-Meteo current conditions pull**: `OpenMeteoAdapter` now implements `IDataPullAdapter` in addition to the existing forecast adapters. A new "Open-Meteo (current conditions)" preset (type=Pull) is available in the data source dialog. It fetches the `current=` endpoint and emits `temperature.outdoor`, `humidity.outdoor`, `pressure`, `cloud.coverage`, `wind.speed`, `wind.direction`, and `wind.gust`. Coordinates fall back to global station settings if not in the source config.
- **Partly cloudy weather animation** (`wa-partly-cloudy`): new animation showing a sun peeking behind a drifting cloud; triggers automatically when `cloud.coverage` is between 20–49 %. The `manualCondition` config option also accepts `"partly-cloudy"` for manual override.

### Fixed
- **Open-Meteo data source presets used wrong provider string**: the "Open-Meteo (forecast)" preset was writing `{"provider":"open-meteo"}` (with hyphen) to the config, but `OpenMeteoAdapter.Provider` is `"openmeteo"` (no hyphen), causing adapter lookup to fail in both the test service and the poll background service. Both presets now write `{"provider":"openmeteo"}`.
- **Open-Meteo current conditions source never polled**: the only Open-Meteo preset set `type=Forecast`, which `DataPullBackgroundService` ignores. The new current-conditions preset sets `type=Pull` so the background service includes it in the poll cycle.
- **Open-Meteo adapter not detected by URL in pull and test services**: `DataPullBackgroundService` and `DataSourceTestService` now detect `api.open-meteo.com` in the source URL and route to the openmeteo adapter, matching the existing behavior in `ForecastRefreshBackgroundService`.

### Fixed
- **Weather animation widget always shows sun**: the `animClass` function only returned `wa-cloud` when wind speed exceeded 50 km/h, so cloudy or overcast conditions always rendered the sun animation. The NWS data-pull adapter now parses `cloudLayers` from the observation response and emits a `cloud.coverage` channel (0–100 %). The animation logic now shows the cloud animation when `cloud.coverage` is ≥ 50 % (scattered or heavier), falling back to the wind-speed check when no cloud data is available.
- **Outside temp widget H/L uses wrong day boundary**: the `/api/weather/daily-extremes` endpoint was using `DateTime.Today` (server local midnight) to bound the calendar day. It now reads `Station.TimeZone` and converts UTC now to station local time, so the high/low resets at local midnight regardless of where the server is hosted.
- **Anonymous users can only see the Default dashboard**: `GetNames`, `GetWidgets`, and `GetLayout` filtered by `OwnerId == null`, so dashboards created by a logged-in admin (stored with their `OwnerId`) were invisible to logged-out users. All three endpoints now skip the owner filter when the request is anonymous, making all dashboards publicly visible.

### Changed
- **Outside temp H/L moved to bottom-left of widget**: the daily high/low temperatures in the Outdoor (and Indoor) Temperature widgets now appear left-aligned at the bottom of the widget card, below the timestamp row, instead of centered in the middle of the card alongside humidity.
- **Hourly and daily forecast widget text and icon scaling**: icons and labels now use container-query units (`cqmin`) so they grow proportionally with widget size; cell minimum widths increased (daily: 56 → 72 px, hourly: 50 → 64 px) for better readability on tablets and larger displays.

## [0.2.0] - 2026-06-23

### Added
- **N/S/E/W labels on wind compass**: the wind widget compass now shows all four cardinal direction labels (N, S, E, W) inside the circle, not just N.
- **Year period on History page**: the period selector now includes "Year" in addition to Day/Week/Month, with the x-axis using monthly granularity. Export also gains the Year option.

### Fixed
- **History chart shows data 4 hours early / cuts off recent readings**: `WeatherReading.Timestamp` is stored as UTC but SQLite returns it with `Kind=Unspecified`. System.Text.Json serialized these without 'Z', so JavaScript's `Date` constructor parsed them as local time, shifting all points by the UTC offset. Fixed by calling `DateTime.SpecifyKind(..., DateTimeKind.Utc)` on all timestamps returned from `GetHistoryAsync`, so the JSON serializer emits 'Z' and browsers interpret them as UTC. The history API endpoint also now treats the parsed date parameter as UTC midnight and clamps `end` to `DateTime.UtcNow`, ensuring today's readings are never excluded by a timezone-shifted midnight boundary.
- **Pressure widget clips "inHg" unit**: the metric row now wraps when value + unit exceed the widget width, so long unit labels like "inHg" drop to the next line instead of being hidden.
- **Wind direction label cut off**: the direction label (N, NNE, NE, etc.) is now shown to the right of the speed value on the same line instead of below it, preventing it from being cut off in shorter widgets.

### Changed
- **History scatter chart is now a single combined chart**: all sensor channels are plotted on one chart instead of one chart per channel. A sidebar on the left lists each channel with a checkbox to show/hide it and a color picker to customize its color. The x-axis time unit adapts to the selected period (hour/day/month). Values are converted to the global unit settings (temperature °C/°F, pressure hPa/inHg/mmHg, wind speed km/h/mph/kn/m/s, rainfall mm/in) and the tooltip shows the unit alongside the value. Timestamps are displayed in the browser's local time zone.

### Added
- **24-hour time format toggle for DateTime widget**: the Add/Edit Widget dialog Simple mode now shows a "Time Format" dropdown (12-hour / 24-hour) for the Date/Time widget type. Previously only configurable via raw JSON (`{"format":"24h"}`).
- **Dew point in Temperature widgets**: the Outdoor and Indoor Temperature widgets now show dew point alongside the humidity secondary line (`dp 12.3°`), in muted text. Dew point is read from the pre-calculated `dewpoint.outdoor` / `dewpoint.indoor` channels.
- **AQI value colored by national standard**: the Air Quality widget's primary AQI number is now colored using the official US AQI color bands — green (0–50), yellow (51–100), orange (101–150), red (151–200), purple (201–300), maroon (301–500).
- **Station lat/lon exposed to front-end**: `window.frcastrConfig` now includes `stationLat` and `stationLon` from `Station.Latitude`/`Station.Longitude` settings. The Radar widget defaults to the station location when no per-widget lat/lon is saved.

### Fixed
- **Rainfall unit label ignores global setting**: `R[14]` only updated the display unit label (`mm`/`in`) when a reading value was present. The unit label is now set from `cfg.rainUnit` unconditionally, so it reflects the global setting even before the first reading arrives.
- **Wind speed unit label ignores global setting**: same pattern as rainfall — `R[6]` now sets the unit label (`km/h`, `mph`, `kn`, `m/s`) from the global setting independent of whether a value is available.
- **Feels Like widget doesn't fill container**: `R[13]` was using fixed Bootstrap font classes (`display-5`, `fs-4`) that don't respond to container queries. It now uses `buildMetric()`, matching all other metric widgets.
- **AirNow preset returns 0 results**: the preset populates `"apiKey": ""` in the data-source config JSON. `??=` null-coalescing doesn't replace empty strings, so the `AirNow.ApiKey` global setting was never read. The adapter now uses `string.IsNullOrWhiteSpace` checks for `apiKey`, `lat`, and `lon` before falling back to global settings.
- **Sunrise/Sunset widget shows no data**: `SunriseSunsetService` and `WeatherController` were calling `double.TryParse` without `CultureInfo.InvariantCulture`. On systems with a non-English locale, `"44.05"` fails to parse (expecting `"44,05"`), causing the service to return null and the widget to show "No sun data". Both now use `NumberStyles.Float, CultureInfo.InvariantCulture`.
- **Pressure trend arrow based on full day instead of 3 hours**: the rising/falling/steady direction was computed from all history points (up to 24 h). It now filters to the last 3 hours before calling `trendDirection`, reflecting recent pressure movement.
- **Pressure/temperature trend arrows were vertical (↑↓)**: changed to diagonal (↗↘) which better conveys gradual rise/fall.

### Changed
- **History page uses scatter plot instead of bar graph**: each raw reading is now plotted as a point at its actual timestamp (x-axis = time, y-axis = value), giving a true picture of reading distribution. Previously readings were aggregated to daily min/avg/max bars.
- **Wind widget layout**: compass now stacks vertically — compass disc on top, speed value and direction label below — for better use of square/wide widget layouts.
- **Metric value font size increased**: `metric-value` scaled from `min(44cqh, 26cqw)` to `min(55cqh, 45cqw)`, filling more horizontal space in wide widgets. Unit and sub-text sizes adjusted proportionally.
- **Last-updated timestamp styling**: widget timestamps are now barely visible (30% opacity) under normal conditions and switch to dark red (`text-danger`) only when data has not updated for 10+ minutes. The intermediate yellow warning state is removed.
- **Radar map hides zoom buttons on mobile**: Leaflet's `+`/`−` zoom controls are hidden on viewports narrower than 768 px; pinch-to-zoom remains available.

### Added
- **Per-widget text color**: the Add/Edit Widget dialogs now have a "Custom text color" toggle with a color picker. The chosen color is stored in the widget's config (`config.color`, saved globally on the `WidgetDefinition`) and drives a `--widget-color` CSS variable on the widget container, coloring both the **title-bar text** and the primary value/number text. Muted units and timestamps are left at their default color for legibility. Leave the toggle off to use the theme default.
- **Combined temperature + humidity widgets**: the Outdoor and Indoor Temperature widgets now show humidity beneath the temperature (humidity rendered at a smaller size). Controlled by `config.showHumidity` (default on) with an optional `config.humidityChannel` override. The standalone Humidity widgets remain available.
- **NWS Current Conditions data-source preset**: Admin → Data Sources now has a "Provider preset" dropdown that fills the Config JSON and sets the source Type for common providers (NWS current/forecast, OpenWeatherMap, WeatherAPI, Open-Meteo, AirNow), so configuring NWS current conditions no longer requires hand-typing `{"provider":"nws-current"}`.
- **Open-Meteo forecast adapter** (`provider: openmeteo`): new `IForecastAdapter` + `IHourlyForecastAdapter` fetches 14-day daily and hourly forecasts from [Open-Meteo](https://open-meteo.com/) (no API key required). Create a Forecast data source with URL `https://api.open-meteo.com/v1/forecast`; lat/lon fall back to `Station.Latitude`/`Station.Longitude`. WMO weather codes are mapped to human-readable condition strings. Supplement or replace NWS for locations with poor NWS coverage.
- **CoordinateSharp for sun and moon times (no API required)**: `SunriseSunsetService` now uses [CoordinateSharp](https://coordinatesharp.com/) (v3.4.1.1) for all celestial calculations — sunrise, sunset, solar noon, civil dawn/dusk, moonrise, moonset, moon illumination, and moon phase — without any external HTTP call. The `sunrisesunset.io` HTTP client registration has been removed. The math-based `SolarCalculator` and `MoonPhaseCalculator` are retained as fallbacks in `WeatherController` when lat/lon are not configured.

### Fixed
- **Golden PM missing; Golden AM showing civil dawn**: the CoordinateSharp migration left `SunriseSunsetService` with `GoldenHourEvening` hardcoded to `null`, and `WeatherController.Sun` mapped Golden AM to civil dawn (sun −6°) rather than golden hour. The service now derives both golden-hour times from the NOAA `SolarCalculator` (sun 6° above the horizon) for the station's coordinates, and the controller maps `GoldenHourMorningEnd`/`GoldenHourEveningStart` to them. `SunriseSunsetResult` gained a `GoldenHourMorning` field.
- **Wind compass doesn't fill the widget**: the compass was sized from `el.clientHeight` measured before layout (often falling back to 44 px) with fixed-size text. The compass disc and needle now scale with the widget via container-query units (`min(72cqh, 45cqw)`), and the speed/direction readout scales to the space beside it, so the widget fills properly at any size.
- **Date/time, temperature, humidity, pressure, rainfall, and AQI widgets didn't scale to fill**: these used fixed Bootstrap font classes (`display-5`, `fs-*`). Values now scale with the widget using container-query units (matching the moon widget), via shared `.metric-value`/`.metric-sub` classes on the `container-type: size` widget body.
- **Pressure trend used an ephemeral client buffer**: the Pressure Trend widget's sparkline and trend were built from an in-memory poll buffer (max 60 points, lost on reload). It now fetches the channel's persisted readings from `/api/weather/history` (cached 60 s per widget) and computes the rising/falling/steady arrow from that logged window, so the trend survives reloads and reflects actual logged data.
- **Moon phase widget doesn't fill widget**: the widget was switching to a compact table layout (tiny 1.8 rem icon) whenever moonrise/moonset data was present, hiding the large phase emoji. The widget now always renders the phase emoji at `clamp(1.5rem, min(55cqh, 70cqw), 12rem)` filling the flex-grow area, with phase name, illumination, and moonrise/moonset times shown compactly below.
- **Hourly forecast stuck at old data**: `ForecastRefreshBackgroundService` was using `ValidUntil` (set 48 hours ahead) to decide whether to re-fetch, so hourly data fetched at 9 PM would not refresh until 9 PM two days later. Refresh decisions now use `FetchedAt` age: daily re-fetches every 3 hours, hourly every 30 minutes. `ValidUntil` is still set to 48 hours as a display-staleness guard and is not used for refresh scheduling.
- **Minimum widget height too tall / mobile squishes widgets more than desktop**: `computeCellHeight` now divides the available viewport height by `maxRows × 2`, halving the per-cell pixel height and the minimum resizable widget height. Mobile now uses the same viewport-height formula instead of the previous column-width ratio: `updateCellHeight` passes the desktop layout's row count as the reference on mobile so both platforms produce the same cell height relative to screen height — widgets look proportionally identical at any resolution. Initial cell height before layout loads reduced from 40 → 20 px and minimum floor from 10 → 5 px.
- **Mobile cell height 1/3 of desktop (landscape tablet)**: `updateCellHeight` on mobile was using `maxRowFromDefs(allDefs)` — the default row span from widget DB definitions — as the reference row count. The actual saved desktop layout is typically much more compact, making the mobile reference row count 3× larger and cell height 3× smaller. `desktopRefRow` is now computed at init from the saved desktop layout (falling back to `maxRowFromDefs` when no layout is saved) and used as the mobile reference, so landscape tablets and desktops produce proportionally identical layouts.
- **Open-Meteo forecast source auto-routed**: `ForecastRefreshBackgroundService` now recognises `api.open-meteo.com` in a source URL and routes to the `openmeteo` adapter automatically, consistent with the existing `api.weather.gov` → `nws` detection.

### Added
- **Moon phase widget shows moonrise and moonset**: when Station latitude/longitude are configured the `/api/weather/moon` endpoint now computes moonrise and moonset times using a simplified Jean Meeus algorithm (moon's mean ecliptic longitude/latitude → equatorial coordinates → hour-angle). Times are displayed in a table below the phase icon/name, matching the layout of the Sunrise/Sunset widget. Use `config.showMoonrise: false` to opt out; `config.format: "24h"` switches to 24-hour clock.

### Fixed
- **Daily (and hourly) forecast not populating after a failed fetch**: `ForecastRefreshBackgroundService` was caching empty `[]` results with the full 48-hour `ValidUntil`, preventing any retry until the cache expired. Empty fetch results are now skipped (not stored), so the next 15-minute tick re-attempts the fetch.
- **Minimum widget height too tall**: `computeCellHeight` fallback reduced from 80 → 40 px and minimum floor from 20 → 10 px, allowing widgets to be resized to a smaller minimum size.
- **Radar map panning still dragging the widget**: replaced the unreliable capture-phase `stopPropagation` approach with `pointerenter`/`pointerleave` listeners that call `frcastrGrid.update(gsItem, { noMove: true/false })` directly on the grid item — GridStack drag is disabled while the pointer is inside the Leaflet container and restored when it leaves.

### Added
- **AirNow AQI adapter**: new `IDataPullAdapter` with `provider: "airnow"` fetches current AQI from the [AirNow API](https://docs.airnowapi.org/). Create a Data Source with Type = AirQuality and Config `{"provider":"airnow","apiKey":"YOUR_KEY"}`. Reports the highest AQI value across all reported parameters as the `aqi.outdoor` channel. API key falls back to the global `AirNow.ApiKey` setting; lat/lon fall back to `Station.Latitude`/`Station.Longitude`.
- **Test button on every data source row**: each row in Admin → Data Sources now has a Test button that opens a result modal without requiring the edit modal first. Test support extended to Pull and AirQuality sources (runs the configured adapter and returns up to 5 sample readings) and Alerts sources (queries the NWS active alerts endpoint for the configured or station coordinates).

### Fixed
- **Logo disappears on publish/deploy**: `deploy.ps1` used `robocopy /MIR` which deleted the `uploads/` directory on the server (and with it the logo file) on every deploy. Added `/XD uploads` to the robocopy exclusion list so the uploads directory is preserved across deployments.
- **Daily forecast widget not showing data**: widget now filters NWS forecast periods to daytime entries only, avoiding nighttime-first periods that have no high-temperature value and render as `–`.
- **Forecast widgets overflow or scroll horizontally**: daily and hourly forecast widgets now measure their container width at render time, fit only as many periods as the available space allows, and distribute them evenly using `flex: 1 1 0` — no wrap, no scrollbar.
- **Moon phase emoji too small**: moon phase widget emoji now scales up to 55% of the container height (capped at 10 rem) instead of a fixed 5 rem maximum, filling the widget properly.
- **Radar widget: map drag moves the widget**: added a capture-phase `pointerdown` listener on the Leaflet container so drag gestures inside the map are never seen by GridStack.
- **Radar widget: zoom/pan not saved**: map `moveend`/`zoomend` events now debounce-write `lat`, `lon`, and `zoom` back to the widget config via `PATCH /api/dashboard/widgets/{id}/config` so position is restored on page reload.
- **Radar widget: ignores light/dark mode**: base tile layer now uses CartoDB Dark Matter in dark mode and OpenStreetMap in light mode; a `MutationObserver` on `#htmlRoot[data-bs-theme]` swaps the layer live when the theme toggles.
- **Radar widget: switched to RainViewer**: replaced NOAA MapServer tiles with [RainViewer](https://www.rainviewer.com/api/weather-maps-api.html). On render the widget fetches the latest available frame from the RainViewer public API and adds it as the radar overlay. A custom `tileUrl` in the widget config overrides this behaviour.
- **Widget minimum height too tall**: GridStack now initialises with `minH: 1` and each widget is added with `minH: 1`, allowing resize down to a single cell. Default height for widgets with no saved value reduced from 3 cells to 2.
- **NWS forecast source with URL returns 0 periods**: when a Forecast source had its URL field set to `https://api.weather.gov/points/{lat},{lon}` and no explicit `provider` in Config, the adapter router defaulted to `generic-json` instead of `nws`, causing the NWS GeoJSON points response to be deserialized as an empty `ForecastPeriod[]`. The router now detects `api.weather.gov` in the URL and routes to the `nws` adapter. The NWS adapter also now extracts lat/lon directly from a `/points/{lat},{lon}` URL as a final fallback when neither Config nor station settings supply coordinates.
- **NWS current conditions data pull adapter** (`provider: nws-current`): create a Pull data source with `{"latitude": "XX.XX", "longitude": "XX.XX"}` in Config to pull live outdoor conditions from the nearest NWS observation station. Writes channels: `temperature.outdoor`, `humidity.outdoor`, `dewpoint.outdoor`, `pressure` (hPa), `wind.speed` (km/h), `wind.direction` (°), `wind.gust` (km/h), `rainfall` (mm). Useful when no local sensors are installed.

### Fixed
- **Sources modal buttons unclickable**: `testResultRow` was placed outside `<div class="modal-body">` and a stray `</div>` pushed `<div class="modal-footer">` outside `<div class="modal-content">`. Bootstrap sets `pointer-events: none` on `.modal-dialog` and restores it only on `.modal-content`, so the Cancel, Test, and Save buttons were never receiving click events. Moved `testResultRow` inside the `row g-3` and removed the extra closing tag.
- **Copy/delete dashboard buttons did nothing**: `JSON.stringify(name)` produces double-quoted strings (e.g. `"Default"`) which were embedded inside double-quoted HTML `onclick="..."` attributes, breaking the attribute at the first inner quote so the handler was never registered. Attributes now use single quotes (`onclick='...'`) so the JSON value is valid inside them.
- **Logo disappears after app pool restart / deployment**: logo files were written to `wwwroot/uploads/` which is overwritten when `dotnet publish` deploys new binaries. Files are now stored in `uploads/` under `ContentRootPath` (outside the published `wwwroot`) and served by a dedicated `UseStaticFiles` call with a `PhysicalFileProvider`. Existing stored paths (`/uploads/logo.ext`) continue to work without any DB change.
- **Dashboard grid causes scrollbar instead of filling viewport**: `computeCellHeight` subtracted only the navbar height from `window.innerHeight` but did not account for the `pt-2` top padding on `#dashboardWrapper`. Now uses `wrapper.getBoundingClientRect().top` as the offset, which captures all spacing regardless of CSS changes.
- **Wind widget content overflows its horizontal borders**: the flex row containing the compass and text had no shrink constraint on the text side, so long speed values could push the row past the widget boundary. The text column now has `min-width:0; overflow:hidden` so it collapses rather than overflows.
- **Radar widget zoom level not applied after first render**: subsequent re-renders called `map.invalidateSize()` only (to handle container resize) and never re-applied the configured `lat`/`lon`/`zoom`. Now calls `map.setView([lat, lon], zoom)` after `invalidateSize()` on every render so config changes take effect immediately.
- **Dragging inside the radar map drags the widget**: GridStack had no `handle` option, making the entire widget item — including the embedded Leaflet map — a drag target. Added `handle: '.widget-titlebar'` so only the title bar initiates widget drag; Leaflet pan gestures now work independently.
- **Navbar logo upload**: upload a PNG/JPG/GIF/SVG/WebP logo in Settings → Branding; the logo replaces the app name text in the navbar. Endpoint: `POST /api/admin/branding/logo`, `DELETE /api/admin/branding/logo`.
- **Full screen button**: ⛶ button in the navbar (dashboard pages only) toggles browser full screen mode.
- **Remember last dashboard**: the last viewed dashboard is stored in `localStorage` so revisiting `/` restores it automatically.
- **Delete dashboard button**: non-default dashboards now show a × delete button in the dashboard dropdown menu.
- **Widget edit button (✏)**: muted pencil button in every widget title bar (admin only) opens a JSON config editor; saves via `PATCH /api/dashboard/widgets/{id}/config`.
- **Widget delete button (✕)**: muted × button in every widget title bar (admin only) replaces the old edit-mode overlay remove button; always visible, un-mutes on hover.
- `Auth.SessionTimeoutHours` setting (0 = 30-day sliding default): configurable in Settings → Auth. Requires app restart to take effect.
- `Branding.Logo` setting seeded automatically.
- **Copy dashboard**: duplicate any dashboard via the dashboard dropdown menu (copy icon button); the new dashboard clones all widget definitions and both desktop/mobile layouts. Endpoint: `POST /api/dashboard/copy?from=Name&to=NewName`.
- **Hourly Forecast widget (type 18)**: new widget type showing hourly forecast periods in a horizontal scrollable strip; hours, condition icon, temperature, and precipitation chance; respects global temperature unit; configurable period count (default 12).
- **NWS hourly forecast**: NwsAdapter now implements `IHourlyForecastAdapter` and fetches the hourly forecast URL from the NWS `/points` response (`properties.forecastHourly`). Daily and hourly results are cached separately (`IsHourly` column added to `ForecastCache`).
- **Radar MapServer data source type** (`DataSourceType.RadarMapServer`): configure a NOAA ArcGIS MapServer URL (e.g. `https://mapservices.weather.noaa.gov/eventdriven/rest/services/radar/radar_base_reflectivity/MapServer`) as a data source; no polling required — tiles are fetched live by the widget.
- **Data source test button**: Test button in the Admin → Data Sources edit modal for Forecast, MQTT, and Radar MapServer sources. Forecast: runs the adapter and returns the first three periods. MQTT: connects, subscribes, and collects up to five messages within 10 seconds. Radar MapServer: requests `{url}?f=json` and validates the ArcGIS service descriptor. Endpoint: `POST /api/admin/datasources/{id}/test`.
- **Widget channel picker (Simple mode)**: Add and Edit Widget modals now have a Simple/JSON toggle. Simple mode shows type-specific form fields — channel dropdown (populated from live readings via `GET /api/admin/channels`) for sensor widgets, speed/direction/gust channel dropdowns for the Wind widget, period count for forecast widgets, and tile URL / lat / lon / zoom / opacity fields for the Radar widget. JSON mode preserves the raw config textarea for advanced users.

### Fixed
- Suppressed false-positive `PendingModelChangesWarning` thrown by EF Core 10.0.9 on startup when the model and migrations are in sync; app was crashing before serving any requests.

### Changed
- **Dashboard layout is now global**: layout changes (drag/resize) apply to all viewers of a dashboard, not just the user who made the change. Anonymous users immediately see the same layout.
- **Dashboard fills the browser window**: cell height is computed dynamically from viewport height minus navbar height so all widgets fill the screen without a scrollbar; recalculates on window resize.
- **Mobile widget ratio**: mobile cell height is computed proportional to column width to preserve the same widget aspect ratio as on desktop.
- **Widget units now follow global settings**: Wind, Pressure, Pressure Trend, Rainfall, and Dew Point widgets default to the global `Display.WindUnit`, `Display.PressureUnit`, and `Display.RainUnit` settings; per-widget `config.unit` / `config.speedUnit` still override the global value.
- **Weather animation scales with widget size**: sun disc, cloud shapes, rain drops, and snowflakes use `cqmin`-based sizing so they grow with larger widgets; rain/snow animation fall distance scales to widget height.
- **Wind compass scales with widget height**: compass diameter adjusts proportionally to available widget space (80 px cap removed).
- **Moon phase scales with widget height**: moon glyph uses `clamp(1.5rem, 15cqh, 5rem)` instead of a fixed `3rem`.
- **"Forecast" widget renamed to "Daily Forecast"**: label updated throughout (widget type dropdown, Widgets admin page, type names map).
- **Daily Forecast period count**: widget edit modal now exposes a "Number of periods" input (1–14) for Daily Forecast and Hourly Forecast widgets; stored as `config.periods`.
- **Radar widget (type 17) uses Leaflet**: replaced static image renderer with a Leaflet.js map; adds an OpenStreetMap base layer and a NOAA radar tile overlay at configurable opacity, latitude, longitude, and zoom. Config keys: `tileUrl`, `lat`, `lon`, `zoom`, `opacity`.
- **Leaflet.js 1.9.4** added to global layout (`_Layout.cshtml`) for the Radar widget.
- **Navbar logo is always displayed beside the app name**: previously only one or the other was shown; both are now rendered together when a logo is configured.

### Fixed
- **Forecast widgets showed no data** after the `AddForecastCacheIsHourly` migration was added: `Program.cs` now calls `Database.Migrate()` at startup so pending migrations are applied automatically when the app pool restarts, without requiring a separate `dotnet ef database update` step.
- Creating or editing a data source returned a 400 error (`dto field is required` / `$.type cannot be converted`) because the ASP.NET Core controller JSON pipeline lacked `JsonStringEnumConverter`. String enum values (e.g. `"Forecast"`) are now accepted for all `[FromBody]` parameters.
- Edit modals on Admin → Data Sources, Widgets, and Webhooks pages showed the wrong Type/Operator value because the Razor-embedded `data-*` JSON serialized enums as integers. All three pages now use `JsonStringEnumConverter` when embedding entity JSON.
- Dew point in Humidity Outdoor and Humidity Indoor widgets was always displayed in °C regardless of the global temperature unit setting.

### Security
- **Login sessions now persist app restarts**: data protection keys are persisted to `data-protection-keys/` on disk (via `PersistKeysToFileSystem`). Previously, keys were in-memory and all auth cookies were invalidated on every restart.

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
