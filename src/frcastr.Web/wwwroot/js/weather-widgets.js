window.WeatherWidgets = (function () {
    'use strict';

    var cfg = window.frcastrConfig || {};
    var pollSecs = cfg.pollIntervalSeconds || 30;
    var offlineSecs = (cfg.sensorOfflineThresholdMinutes || 10) * 60;

    // ── Helpers ──────────────────────────────────────────────────────────────

    function reading(data, channel) {
        return (data && data.current && data.current.readings && data.current.readings[channel]) || null;
    }

    // "temperature.indoor@indoor-01" -> "@indoor-01"; empty for station-wide keys.
    function deviceSuffix(key) {
        var i = key ? key.indexOf('@') : -1;
        return i > 0 ? key.slice(i) : '';
    }

    // Companion channels follow the device of the channel they accompany, falling back to the
    // station-wide reading for devices that report only some of a widget's channels.
    function companionReading(data, channel, suffix) {
        return (suffix ? reading(data, channel + suffix) : null) || reading(data, channel);
    }

    function val(r) { return r != null ? Number(r.value) : null; }
    function unit(r) { return (r && r.unit) || ''; }
    function ts(r) { return (r && r.timestamp) || null; }

    function toF(c) { return c * 9 / 5 + 32; }
    function toInHg(hpa) { return hpa * 0.02953; }
    function toMmHg(hpa) { return hpa * 0.75006; }
    function toMph(kmh) { return kmh * 0.62137; }
    function toKnots(kmh) { return kmh * 0.53996; }
    function toIn(mm) { return mm * 0.03937; }

    function fmt(v, dec) {
        if (v == null) return '–';
        return Number(v).toFixed(dec != null ? dec : 1);
    }

    function utcTs(isoTs) {
        if (!isoTs) return null;
        if (isoTs.endsWith('Z')) return isoTs;
        // DateTimeOffset strings already carry an offset (e.g. +00:00); don't append Z
        if (/[+-]\d{2}:\d{2}$/.test(isoTs)) return isoTs;
        return isoTs + 'Z';
    }

    function ageMs(isoTs) {
        var t = utcTs(isoTs);
        if (!t) return Infinity;
        return Date.now() - new Date(t).getTime();
    }

    function isNightNow(sun) {
        if (!sun || !sun.sunrise || !sun.sunset) return false;
        var rise = new Date(utcTs(sun.sunrise)).getTime();
        var set  = new Date(utcTs(sun.sunset)).getTime();
        if (isNaN(rise) || isNaN(set)) return false;
        var now = Date.now();
        return now < rise || now > set;
    }

    function ageStr(isoTs) {
        var ms = ageMs(isoTs);
        if (ms === Infinity) return '';
        var secs = Math.round(ms / 1000);
        if (secs < 60)   return secs + 's ago';
        if (secs < 3600) return Math.floor(secs / 60) + 'm ago';
        return Math.floor(secs / 3600) + 'h ago';
    }

    function ageClass(isoTs) {
        var ms = ageMs(isoTs);
        if (ms === Infinity) return 'ts-fresh';
        return (ms / 1000 >= offlineSecs) ? 'text-danger' : 'ts-fresh';
    }

    function tsHtml(isoTs) {
        if (!isoTs) return '';
        var age = ageStr(isoTs);
        if (!age) return '';
        return '<div class="mt-auto pt-1 small ' + ageClass(isoTs) + '">' + age + '</div>';
    }

    function windDirLabel(deg) {
        if (deg == null) return '–';
        var dirs = ['N','NNE','NE','ENE','E','ESE','SE','SSE','S','SSW','SW','WSW','W','WNW','NW','NNW'];
        return dirs[Math.round(Number(deg) / 22.5) % 16];
    }

    function conditionIcon(cond) {
        if (!cond) return '🌡️';
        var c = cond.toLowerCase();
        if (c.indexOf('thunder') >= 0 || c.indexOf('lightning') >= 0) return '⛈️';
        if (c.indexOf('blizzard') >= 0) return '🌨️';
        if (c.indexOf('snow') >= 0)     return '❄️';
        if (c.indexOf('sleet') >= 0 || c.indexOf('freez') >= 0 || c.indexOf('ice') >= 0) return '🌨️';
        if (c.indexOf('rain') >= 0 || c.indexOf('shower') >= 0 || c.indexOf('drizzle') >= 0) return '🌧️';
        if (c.indexOf('fog') >= 0 || c.indexOf('mist') >= 0 || c.indexOf('haze') >= 0) return '🌫️';
        if (c.indexOf('overcast') >= 0) return '☁️';
        if (c.indexOf('cloud') >= 0)    return '⛅';
        if (c.indexOf('clear') >= 0 || c.indexOf('sunny') >= 0 || c.indexOf('fair') >= 0) return '☀️';
        if (c.indexOf('wind') >= 0 || c.indexOf('breezy') >= 0 || c.indexOf('gusty') >= 0) return '💨';
        return '🌡️';
    }

    function aqiCategory(v) {
        if (v == null) return null;
        if (v <=  50) return { label: 'Good',          cls: 'success', color: '#00e400' };
        if (v <= 100) return { label: 'Moderate',       cls: 'warning', color: '#ffff00' };
        if (v <= 150) return { label: 'USG',            cls: 'warning', color: '#ff7e00' };
        if (v <= 200) return { label: 'Unhealthy',      cls: 'danger',  color: '#ff0000' };
        if (v <= 300) return { label: 'Very Unhealthy', cls: 'danger',  color: '#8f3f97' };
        return              { label: 'Hazardous',       cls: 'danger',  color: '#7e0023' };
    }

    function fmtTime(isoTs, use12h) {
        if (!isoTs) return '–';
        try {
            var d = new Date(utcTs(isoTs));
            if (isNaN(d.getTime())) return '–';
            if (use12h) {
                var h = d.getHours(), m = d.getMinutes();
                var ampm = h >= 12 ? 'PM' : 'AM';
                h = h % 12 || 12;
                return h + ':' + pad(m) + ' ' + ampm;
            }
            return pad(d.getHours()) + ':' + pad(d.getMinutes());
        } catch (e) { return '–'; }
    }

    function pad(n) { return n < 10 ? '0' + n : String(n); }

    function escHtml(s) {
        return String(s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
    }

    function trendArrow(direction) {
        if (!direction) return '';
        if (direction === 'Rising')  return '<span class="text-danger ms-1" title="Rising">&#8599;</span>';
        if (direction === 'Falling') return '<span class="text-info ms-1" title="Falling">&#8600;</span>';
        return '<span class="text-body-secondary ms-1" title="Steady">&#8594;</span>';
    }

    function sparkline(canvas, points, color) {
        if (!canvas || !window.Chart) return;
        var existing = Chart.getChart(canvas);
        if (existing) {
            existing.data.datasets[0].data = points;
            existing.update('none');
            return;
        }
        new Chart(canvas, {
            type: 'line',
            data: {
                labels: points.map(function (_, i) { return i; }),
                datasets: [{
                    data: points,
                    borderColor: color || 'rgb(99,102,241)',
                    borderWidth: 1.5,
                    fill: false,
                    pointRadius: 0,
                    tension: 0.3
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                plugins: { legend: { display: false }, tooltip: { enabled: false } },
                scales: { x: { display: false }, y: { display: false } }
            }
        });
    }

    // ── Animation builder ────────────────────────────────────────────────────

    function animClass(readings, config) {
        if (config.conditionSource === 'manual' && config.manualCondition) {
            return 'wa-' + config.manualCondition;
        }
        var precip       = Number((readings['rainfall']             || {}).value || 0);
        var temp         = val({ value: (readings['temperature.outdoor'] || {}).value || 15 });
        var hasLightning = readings['lightning'] != null;
        var wind         = Number((readings['wind.speed']           || {}).value || 0);
        var cloudRaw     = (readings['cloud.coverage'] || {}).value;
        var cloud        = cloudRaw != null ? Number(cloudRaw) : -1;
        if (hasLightning)            return 'wa-lightning';
        if (precip > 0 && temp <= 2) return 'wa-snow';
        if (precip > 0)              return 'wa-rain';
        if (cloud > 65)              return 'wa-cloud';
        if (wind > 50)               return 'wa-cloud';
        if (cloud >= 20)             return 'wa-partly-cloudy';
        return 'wa-sun';
    }

    function discHtml(night, moonIcon) {
        if (!night) return '<div class="wa-sun-disc"></div>';
        return '<div class="wa-sun-disc wa-moon-disc"><span class="wa-moon-icon">' +
            escHtml(moonIcon || '🌙') + '</span></div>';
    }

    function buildAnimHtml(cls, night, moonIcon) {
        var wrapCls = cls + (night ? ' wa-night' : '');
        if (cls === 'wa-sun') {
            return '<div class="weather-anim ' + wrapCls + '">' + discHtml(night, moonIcon) + '</div>';
        }
        if (cls === 'wa-partly-cloudy') {
            return '<div class="weather-anim ' + wrapCls + '">' +
                '<div class="wa-pc-scene">' +
                discHtml(night, moonIcon) +
                '<div class="wa-cloud-shape wa-pc-cloud"></div>' +
                '</div>' +
                '</div>';
        }
        if (cls === 'wa-cloud') {
            return '<div class="weather-anim ' + wrapCls + '">' +
                '<div class="wa-cloud-shape wa-cloud-back"></div>' +
                '<div class="wa-cloud-shape wa-cloud-front"></div>' +
                '</div>';
        }
        var drops = '';
        var isSnow = cls === 'wa-snow';
        for (var i = 0; i < 8; i++) {
            var offset = (10 + i * 9) + '%';
            var delay  = (i * (isSnow ? 0.22 : 0.12)).toFixed(2) + 's';
            var el     = isSnow
                ? '<span class="wa-flake" style="--offset:' + offset + ';--delay:' + delay + '">&#10052;</span>'
                : '<span class="wa-drop"  style="--offset:' + offset + ';--delay:' + delay + '"></span>';
            drops += el;
        }
        return '<div class="weather-anim ' + wrapCls + '">' +
            '<div class="wa-cloud-shape"></div>' +
            (cls === 'wa-lightning'
                ? '<div class="wa-bolt">&#9889;</div>'
                : '<div class="wa-drops">' + drops + '</div>') +
            '</div>';
    }

    // ── Metric builder ─────────────────────────────────────────────────────────
    // Container-query–scaled value display. The .widget-body is a `container-type:size`
    // context (see site.css), so .metric-value / .metric-sub font sizes scale to fill
    // the widget. `secondary` lines render smaller than the primary value.

    function buildMetric(parts) {
        var unitHtml = parts.unit
            ? '<span class="text-body-secondary metric-unit">' + parts.unit + '</span>'
            : '';
        var colorStyle = parts.valueColor ? ' style="color:' + parts.valueColor + '"' : '';
        var secondary = (parts.secondary || []).join('');
        var hiLow = parts.hiLow
            ? '<div class="metric-hl text-body-secondary d-flex gap-2">' + parts.hiLow + '</div>'
            : '';
        return '<div class="metric d-flex flex-column h-100" style="min-height:0;overflow:hidden">' +
            '<div class="metric-fill d-flex flex-column justify-content-center flex-grow-1" style="min-height:0">' +
            '<div class="metric-primary d-flex align-items-baseline gap-1" style="line-height:1;min-width:0;flex-wrap:wrap">' +
            '<span class="fw-bold metric-value"' + colorStyle + '>' + parts.value + '</span>' +
            unitHtml +
            (parts.trend || '') +
            '</div>' +
            secondary +
            '</div>' +
            hiLow +
            (parts.footer || '') +
            '</div>';
    }

    // ── Renderers ─────────────────────────────────────────────────────────────
    // Each receives (el, config, data) where el is the [data-widget-body] div.

    var R = {};

    // 0: DateTime
    R[0] = function (el, config, data) {
        var use12h   = config.format !== '24h';
        var showDate = config.showDate !== false;
        var tzId     = config.timezone === 'station' ? null : (config.timezone || null);
        var now      = new Date();

        var timeStr, dateStr = '';
        try {
            timeStr = now.toLocaleTimeString(undefined, {
                hour: 'numeric', minute: '2-digit', second: '2-digit',
                hour12: use12h,
                timeZone: tzId || undefined
            });
            if (showDate) {
                dateStr = now.toLocaleDateString(undefined, {
                    weekday: 'long', year: 'numeric', month: 'long', day: 'numeric',
                    timeZone: tzId || undefined
                });
            }
        } catch (e) {
            timeStr = now.toLocaleTimeString();
            if (showDate) dateStr = now.toLocaleDateString();
        }

        // Update in-place to avoid layout thrash on 1s tick
        if (!el.querySelector('[data-clock]')) {
            el.innerHTML =
                '<div class="d-flex flex-column align-items-center justify-content-center h-100 text-center gap-1" style="min-height:0;overflow:hidden">' +
                '<div class="fw-bold lh-1 metric-value" data-clock></div>' +
                (showDate ? '<div class="text-body-secondary metric-sub text-truncate w-100" data-caldate></div>' : '') +
                '</div>';
        }
        var clockEl = el.querySelector('[data-clock]');
        var dateEl  = el.querySelector('[data-caldate]');
        if (clockEl) clockEl.textContent = timeStr;
        if (dateEl)  dateEl.textContent  = dateStr;
    };

    // 1: Temperature Outdoor (+ combined humidity + dew point)
    R[1] = function (el, config, data) {
        renderTempHumidity(el, config, data, 'temperature.outdoor', 'humidity.outdoor', 'dewpoint.outdoor');
    };

    // 2: Temperature Indoor (+ combined humidity)
    R[2] = function (el, config, data) {
        renderTempHumidity(el, config, data, 'temperature.indoor', 'humidity.indoor', null);
    };

    // Shared temp (+ optional humidity + dew point) renderer. A null dewCh omits the dew point
    // outright; where one is offered, config.showDewpoint can still hide it, matching widget 3/4.
    function renderTempHumidity(el, config, data, tempCh, humCh, dewCh) {
        var ch    = config.channel || tempCh;
        var sfx   = deviceSuffix(ch);
        var r     = reading(data, ch);
        var v     = val(r);
        var u     = window.getTempUnit ? window.getTempUnit() : (config.unit || 'C');
        var dv    = v == null ? null : (u === 'F' ? toF(v) : v);
        var trend = (data && data.current && data.current.trends && data.current.trends[ch]) || null;

        var secondary = [];
        if (config.showHumidity !== false) {
            var hr  = config.humidityChannel
                ? reading(data, config.humidityChannel)
                : companionReading(data, humCh, sfx);
            var hv  = val(hr);
            var dpD = null;
            if (dewCh && config.showDewpoint !== false) {
                var dpV = val(companionReading(data, dewCh, sfx));
                dpD = dpV != null ? (u === 'F' ? toF(dpV) : dpV) : null;
            }
            if (hv != null) {
                secondary.push(
                    '<div class="metric-sub d-flex align-items-baseline gap-1">' +
                    '<span class="fw-semibold">' + fmt(hv, 0) + '</span>' +
                    '<span class="text-body-secondary metric-sub-unit">%</span>' +
                    (dpD != null ? '<span class="text-body-secondary metric-sub-unit ms-2">dp&thinsp;' + fmt(dpD, 0) + '&deg;</span>' : '') +
                    '</div>');
            }
        }

        var hiLow = null;
        var chEx = data && data.dailyExtremes && data.dailyExtremes[ch];
        if (chEx && (chEx.max != null || chEx.min != null)) {
            var hiD = chEx.max != null ? (u === 'F' ? toF(chEx.max) : chEx.max) : null;
            var loD = chEx.min != null ? (u === 'F' ? toF(chEx.min) : chEx.min) : null;
            hiLow = (hiD != null ? '<span>H&thinsp;' + fmt(hiD, 0) + '&deg;</span>' : '') +
                    (loD != null ? '<span>L&thinsp;' + fmt(loD, 0) + '&deg;</span>' : '') || null;
        }

        el.innerHTML = buildMetric({
            value:     fmt(dv, 0),
            unit:      '&deg;' + u,
            trend:     trend ? trendArrow(trend.direction) : '',
            secondary: secondary,
            hiLow:     hiLow,
            footer:    tsHtml(ts(r))
        });
    }

    // 3: Humidity Outdoor
    R[3] = function (el, config, data) {
        renderHumidity(el, config, data, 'humidity.outdoor', 'dewpoint.outdoor');
    };

    // 4: Humidity Indoor
    R[4] = function (el, config, data) {
        renderHumidity(el, config, data, 'humidity.indoor', 'dewpoint.indoor');
    };

    function renderHumidity(el, config, data, humCh, dewCh) {
        var ch     = config.channel || humCh;
        var r      = reading(data, ch);
        var v      = val(r);
        var tu     = window.getTempUnit ? window.getTempUnit() : 'C';
        var showDp = config.showDewpoint !== false;
        // Follow the bound device, so a widget on "humidity.indoor@attic-02" pairs the reading
        // with that room's dew point rather than the station's.
        var dpR    = showDp ? companionReading(data, dewCh, deviceSuffix(ch)) : null;
        var dpV    = val(dpR);
        var dpD    = dpV != null ? (tu === 'F' ? toF(dpV) : dpV) : null;

        var secondary = dpD != null
            ? ['<div class="metric-sub text-body-secondary text-truncate">Dew point: ' + fmt(dpD, 0) + '&deg;' + tu + '</div>']
            : [];

        el.innerHTML = buildMetric({
            value:     fmt(v, 0),
            unit:      '%',
            secondary: secondary,
            footer:    tsHtml(ts(r))
        });
    }

    // 5: Pressure
    R[5] = function (el, config, data) {
        var ch  = config.channel || 'pressure';
        var r   = reading(data, ch);
        var v   = val(r);
        var u   = config.unit || cfg.pressureUnit || 'hPa';
        var dv  = v, ul = 'hPa', dec = 0;
        if (v != null && u === 'inHg') { dv = toInHg(v); ul = 'inHg'; dec = 2; }
        else if (v != null && u === 'mmHg') { dv = toMmHg(v); ul = 'mmHg'; dec = 0; }
        el.innerHTML = buildMetric({
            value:  fmt(dv, dec),
            unit:   ul,
            footer: tsHtml(ts(r))
        });
    };

    // 6: Wind
    R[6] = function (el, config, data) {
        var sCh = config.speedChannel     || 'wind.speed';
        var dCh = config.directionChannel || 'wind.direction';
        var sR  = reading(data, sCh);
        var dR  = reading(data, dCh);
        var sv  = val(sR), dv = val(dR);
        var u   = config.speedUnit || cfg.windUnit || 'kmh';
        var ul  = u === 'mph' ? 'mph' : u === 'knots' ? 'kn' : u === 'ms' ? 'm/s' : 'km/h';
        var ds  = sv;
        if (sv != null) {
            if (u === 'mph')    ds = toMph(sv);
            else if (u === 'knots') ds = toKnots(sv);
            else if (u === 'ms')    ds = sv / 3.6;
        }
        var dirLabel  = windDirLabel(dv);
        var rotateCss = dv != null
            ? 'transform:rotate(' + dv + 'deg);'
            : 'visibility:hidden;';
        el.innerHTML =
            '<div class="metric d-flex flex-column h-100 align-items-center justify-content-center text-center" style="min-height:0;overflow:hidden;gap:0.25cqh">' +
            '<div class="wind-compass flex-shrink-0">' +
            '<div class="wind-compass-n">N</div>' +
            '<div class="wind-compass-s">S</div>' +
            '<div class="wind-compass-e">E</div>' +
            '<div class="wind-compass-w">W</div>' +
            '<div class="wind-compass-needle" style="' + rotateCss + '"></div>' +
            '</div>' +
            '<div class="d-flex align-items-center justify-content-center gap-2" style="line-height:1">' +
            '<span class="fw-bold wind-speed-val">' + fmt(ds) + '</span>' +
            '<span class="text-body-secondary wind-speed-unit">' + ul + '</span>' +
            '<span class="text-body-secondary metric-sub">' + dirLabel + '</span>' +
            '</div>' +
            tsHtml(ts(sR)) +
            '</div>';
    };

    // 7: Weather Animation
    R[7] = function (el, config, data) {
        var readings = (data && data.current && data.current.readings) || {};
        var cls   = animClass(readings, config);
        var night = isNightNow(data && data.sun);
        el.innerHTML = buildAnimHtml(cls, night, data && data.moon && data.moon.icon);
    };

    // 8: Forecast
    R[8] = function (el, config, data) {
        var periods = (data && data.forecast && data.forecast.aggregated) || [];
        var max  = Number(config.periods) || 5;
        var u    = window.getTempUnit ? window.getTempUnit() : (config.unit || 'C');

        // Prefer daytime periods so NWS night-first alternation doesn't show lows
        var dayPeriods = periods.filter(function (p) { return p.isDaytime !== false; });
        var source = dayPeriods.length ? dayPeriods : periods;

        // Fit as many items as the container width allows, up to configured max
        var availWidth = el.getBoundingClientRect().width || 300;
        var maxFit = Math.max(1, Math.floor(availWidth / 72));
        var list = source.slice(0, Math.min(max, maxFit));

        if (!list.length) {
            el.innerHTML = '<div class="d-flex align-items-center justify-content-center h-100 text-body-secondary small">No forecast data</div>';
            return;
        }

        var html = '<div class="d-flex h-100 align-items-center overflow-hidden">';
        list.forEach(function (p) {
            var d    = new Date(utcTs(p.periodStart) || p.periodStart);
            var day  = d.toLocaleDateString(undefined, { weekday: 'short' });
            var icon = conditionIcon(p.condition);
            var temp = p.temperature != null
                ? fmt(u === 'F' ? toF(p.temperature) : p.temperature, 0) + '&deg;' + u
                : '&ndash;';
            var prcp = p.precipChance != null ? Math.round(p.precipChance) + '%' : '';
            html +=
                '<div class="d-flex flex-column align-items-center gap-1" style="flex:1 1 0;min-width:0;overflow:hidden">' +
                '<div class="text-body-secondary text-truncate w-100 text-center" style="font-size:clamp(0.75rem,3cqmin,1rem)">' + escHtml(day) + '</div>' +
                '<div style="font-size:clamp(1.5rem,7cqmin,3.5rem);line-height:1">' + icon + '</div>' +
                '<div class="fw-semibold" style="font-size:clamp(0.875rem,3.5cqmin,1.4rem)">' + temp + '</div>' +
                (prcp ? '<div class="text-info" style="font-size:clamp(0.7rem,2.8cqmin,1rem)">' + prcp + '</div>' : '') +
                '</div>';
        });
        html += '</div>';
        el.innerHTML = html;
    };

    // 9: Moon
    R[9] = function (el, config, data) {
        var moon = data && data.moon;
        if (!moon) {
            el.innerHTML = '<div class="d-flex align-items-center justify-content-center h-100 text-body-secondary">–</div>';
            return;
        }
        var showIllum = config.showIllumination !== false;
        var showName  = config.showPhaseName !== false;
        var showRise  = config.showMoonrise !== false;
        var use12h    = config.format !== '24h';
        var illum     = moon.illumination != null ? Math.round(moon.illumination * 100) + '%' : '';

        var footer = '';
        if (showName && moon.phaseName)
            footer += '<div class="small fw-semibold text-truncate">' + escHtml(moon.phaseName) + '</div>';
        if (showIllum && illum)
            footer += '<div class="x-small text-body-secondary">' + illum + ' lit</div>';
        if (showRise && (moon.moonrise || moon.moonset)) {
            var rStr = moon.moonrise ? '&#127765;&thinsp;' + fmtTime(moon.moonrise, use12h) : '';
            var sStr = moon.moonset  ? '&#127761;&thinsp;' + fmtTime(moon.moonset,  use12h) : '';
            footer +=
                '<div class="x-small text-body-secondary d-flex flex-wrap gap-2 justify-content-center">' +
                (rStr ? '<span>' + rStr + '</span>' : '') +
                (sStr ? '<span>' + sStr + '</span>' : '') +
                '</div>';
        }

        el.innerHTML =
            '<div class="d-flex flex-column align-items-center h-100 text-center" style="min-height:0">' +
            '<div class="flex-grow-1 d-flex align-items-center justify-content-center" style="min-height:0;overflow:hidden">' +
            '<div style="font-size:clamp(1.5rem,min(55cqh,70cqw),12rem);line-height:1">' + escHtml(moon.icon || '🌙') + '</div>' +
            '</div>' +
            (footer ? '<div class="d-flex flex-column align-items-center gap-1 pb-1 w-100">' + footer + '</div>' : '') +
            '</div>';
    };

    // 10: Custom channel
    R[10] = function (el, config, data) {
        var ch   = config.channel || '';
        var r    = ch ? reading(data, ch) : null;
        var v    = val(r);
        var u    = config.unit || unit(r) || '';
        var dec  = config.decimalPlaces != null ? Number(config.decimalPlaces) : 1;
        el.innerHTML =
            '<div class="d-flex flex-column h-100">' +
            '<div class="d-flex align-items-baseline gap-1">' +
            '<span class="display-5 fw-bold">' + fmt(v, dec) + '</span>' +
            (u ? '<span class="fs-5 text-body-secondary">' + escHtml(u) + '</span>' : '') +
            '</div>' +
            tsHtml(ts(r)) +
            '</div>';
    };

    // 11: Sunrise / Sunset
    R[11] = function (el, config, data) {
        var sun = data && data.sun;
        if (!sun) {
            el.innerHTML = '<div class="d-flex align-items-center justify-content-center h-100 text-body-secondary small">No sun data</div>';
            return;
        }
        var use12h   = config.format !== '24h';
        var showG    = config.showGoldenHour !== false;
        var showL    = config.showDayLength  !== false;
        var showN    = !!config.showSolarNoon;
        var rows = [];
        if (sun.sunrise)               rows.push(['&#127749; Sunrise',    fmtTime(sun.sunrise, use12h)]);
        if (showN && sun.solarNoon)    rows.push(['&#9728;&#65039; Solar noon', fmtTime(sun.solarNoon, use12h)]);
        if (sun.sunset)                rows.push(['&#127751; Sunset',     fmtTime(sun.sunset, use12h)]);
        if (showG && sun.goldenHourMorningEnd)   rows.push(['&#127748; Golden AM', fmtTime(sun.goldenHourMorningEnd, use12h)]);
        if (showG && sun.goldenHourEveningStart) rows.push(['&#127748; Golden PM', fmtTime(sun.goldenHourEveningStart, use12h)]);
        if (showL && sun.sunrise && sun.sunset) {
            var diffMs = new Date(utcTs(sun.sunset)) - new Date(utcTs(sun.sunrise));
            if (diffMs > 0) {
                var totalMin = Math.round(diffMs / 60000);
                var dlH = Math.floor(totalMin / 60);
                var dlM = totalMin % 60;
                rows.push(['&#9203; Day length', dlH + 'h ' + String(dlM).padStart(2, '0') + 'm']);
            }
        }
        var tableRows = rows.map(function (row) {
            return '<tr><td class="pe-3 small text-body-secondary">' + row[0] + '</td>' +
                   '<td class="small fw-semibold">' + escHtml(row[1]) + '</td></tr>';
        }).join('');
        el.innerHTML = '<div class="overflow-auto h-100"><table class="table table-sm table-borderless mb-0">' + tableRows + '</table></div>';
    };

    // 12: Alerts
    R[12] = function (el, config, data) {
        var alerts = (data && data.alerts) || [];
        var max    = Number(config.maxCount) || 3;
        var showEx = !!config.showExpiry;
        if (!Array.isArray(alerts) || !alerts.length) {
            el.innerHTML = '<div class="d-flex align-items-center justify-content-center h-100 text-body-secondary small">No active alerts</div>';
            return;
        }
        var sevCls = { extreme: 'danger', severe: 'warning', moderate: 'info', minor: 'secondary' };
        var html = '<div class="overflow-auto h-100 d-flex flex-column gap-1">';
        alerts.slice(0, max).forEach(function (a) {
            var cls   = sevCls[(a.severity || '').toLowerCase()] || 'secondary';
            var expiry = showEx && a.expires ? '<div class="small">Expires: ' + fmtTime(a.expires, true) + '</div>' : '';
            html +=
                '<div class="alert alert-' + cls + ' py-1 px-2 mb-0">' +
                '<div class="fw-semibold small">' + escHtml(a.event || 'Alert') + '</div>' +
                (a.headline ? '<div class="small">' + escHtml(a.headline) + '</div>' : '') +
                expiry +
                '</div>';
        });
        html += '</div>';
        el.innerHTML = html;
    };

    // 13: Feels Like
    R[13] = function (el, config, data) {
        // The editor offers this widget a channel picker (SIMPLE_CHANNEL_TYPES), so honor it —
        // it was saved to config and then ignored, which mattered once feels-like started being
        // published per device as well as station-wide.
        var r  = reading(data, config.channel || 'feelslike.outdoor');
        var v  = val(r);
        var u  = window.getTempUnit ? window.getTempUnit() : (config.unit || 'C');
        var dv = v == null ? null : (u === 'F' ? toF(v) : v);
        el.innerHTML = buildMetric({
            value:  fmt(dv, 0),
            unit:   '&deg;' + u,
            footer: tsHtml(ts(r))
        });
    };

    // 14: Rainfall
    R[14] = function (el, config, data) {
        var ch  = config.channel || 'rainfall';
        var r   = reading(data, ch);
        var v   = val(r);
        var u   = config.unit || cfg.rainUnit || 'mm';
        var ul  = (u === 'in') ? 'in' : 'mm';
        var dv  = (v != null && u === 'in') ? toIn(v) : v;
        el.innerHTML = buildMetric({
            value:  fmt(dv),
            unit:   ul,
            footer: tsHtml(ts(r))
        });
    };

    // 15: Pressure Trend (sparkline) — sourced from logged readings on the server,
    // so the trend reflects persisted history rather than the in-memory poll buffer.
    R[15] = function (el, config, data) {
        var ch  = config.channel || 'pressure';
        var r   = reading(data, ch);
        var v   = val(r);
        var u   = config.unit || cfg.pressureUnit || 'hPa';
        var dv  = v, ul = 'hPa', dec = 0;
        if (v != null && u === 'inHg') { dv = toInHg(v); ul = 'inHg'; dec = 2; }
        else if (v != null && u === 'mmHg') { dv = toMmHg(v); ul = 'mmHg'; dec = 0; }

        function conv(hpa) {
            if (u === 'inHg') return toInHg(hpa);
            if (u === 'mmHg') return toMmHg(hpa);
            return hpa;
        }

        var wEl    = el.closest('[data-widget-id]');
        var wId    = (wEl && wEl.dataset.widgetId) || 'x';
        var cvsId  = 'chart-press-' + wId;

        if (!el.querySelector('[data-press-val]')) {
            el.innerHTML =
                '<div class="d-flex align-items-baseline gap-1 mb-1" style="line-height:1">' +
                '<span class="fw-bold metric-value-sm" data-press-val></span>' +
                '<span class="small text-body-secondary">' + ul + '</span>' +
                '<span data-press-trend></span>' +
                '</div>' +
                '<div class="flex-grow-1" style="min-height:40px;position:relative">' +
                '<canvas id="' + cvsId + '"></canvas>' +
                '</div>' +
                '<div class="mt-1 small" data-press-ts></div>';
        }

        var valEl   = el.querySelector('[data-press-val]');
        var tsEl    = el.querySelector('[data-press-ts]');
        var trendEl = el.querySelector('[data-press-trend]');
        if (valEl) valEl.textContent = fmt(dv, dec);
        if (tsEl) {
            tsEl.textContent  = ageStr(ts(r));
            tsEl.className    = 'mt-1 small ' + ageClass(ts(r));
        }

        // Pull persisted history (hPa) from the server; cached briefly per widget element.
        var THREE_H = 3 * 60 * 60 * 1000;
        fetchLoggedHistory(el, ch, function (raw) {
            var points = raw.map(function (h) { return conv(h.v); });
            var canvas = document.getElementById(cvsId);
            sparkline(canvas, points, 'rgb(99,102,241)');

            if (trendEl) {
                var recent = raw.filter(function (h) { return Date.now() - h.t < THREE_H; });
                var dir = trendDirection(recent.map(function (h) { return h.v; }), 0.6);
                trendEl.innerHTML = dir ? trendArrow(dir) : '';
            }
        });
    };

    // Fetch logged readings for a channel from the history API. Results are cached on the
    // element for 60s so repeated renders (poll/resize) don't spam the endpoint.
    function fetchLoggedHistory(el, channel, cb) {
        var now = Date.now();
        if (el._histChannel === channel && el._histData && (now - el._histAt) < 60000) {
            cb(el._histData);
            return;
        }
        if (el._histData) cb(el._histData); // render stale immediately while refreshing
        if (el._histFetching) return;
        el._histFetching = true;
        fetch('/api/weather/history?period=day&channels=' + encodeURIComponent(channel))
            .then(function (r) { return r.json(); })
            .then(function (res) {
                var raw = ((res && res.rawPoints) || [])
                    .filter(function (p) { return p.channelName === channel; })
                    .map(function (p) { return { t: new Date(utcTs(p.timestamp)).getTime(), v: Number(p.value) }; });
                el._histChannel  = channel;
                el._histData     = raw;
                el._histAt       = Date.now();
                el._histFetching = false;
                cb(raw);
            })
            .catch(function () { el._histFetching = false; });
    }

    // Direction over the logged window: compare newest vs oldest against a unit threshold.
    function trendDirection(values, threshold) {
        if (!values || values.length < 2) return null;
        var delta = values[values.length - 1] - values[0];
        if (delta > threshold)  return 'Rising';
        if (delta < -threshold) return 'Falling';
        return 'Steady';
    }

    // 16: Air Quality
    R[16] = function (el, config, data) {
        var ch   = config.channel || 'aqi.outdoor';
        var r    = reading(data, ch);
        var v    = val(r);
        var cat  = v != null ? aqiCategory(Math.round(v)) : null;
        var show = config.showCategory !== false;
        var secondary = (cat && show)
            ? ['<div class="metric-sub"><span class="badge text-bg-' + cat.cls + '">' + cat.label + '</span></div>']
            : [];
        el.innerHTML = buildMetric({
            value:      fmt(v, 0),
            unit:       'AQI',
            valueColor: cat ? cat.color : null,
            secondary:  secondary,
            footer:     tsHtml(ts(r))
        });
    };

    // 17: Radar (Leaflet + RainViewer by default; custom tileUrl overrides)
    R[17] = function (el, config, data) {
        if (!window.L) {
            el.innerHTML = '<div class="d-flex align-items-center justify-content-center h-100 text-body-secondary small">Leaflet not loaded</div>';
            return;
        }

        var lat     = Number(config.lat)     || Number(cfg.stationLat) || 39.5;
        var lon     = Number(config.lon)     || Number(cfg.stationLon) || -98.35;
        var zoom    = Number(config.zoom)    || 5;
        var opacity = config.opacity != null ? Number(config.opacity) : 0.6;
        var customTile = config.tileUrl || null;  // explicit tile URL skips RainViewer

        // Reuse existing map on simple resizes (don't reset view)
        if (el._leafletMap) {
            el._leafletMap.invalidateSize();
            return;
        }

        function getBaseTileUrl() {
            var htmlEl = document.getElementById('htmlRoot');
            var isDark = htmlEl && htmlEl.getAttribute('data-bs-theme') === 'dark';
            return isDark
                ? 'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png'
                : 'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png';
        }

        el.style.padding = '0';
        var isMobileView = window.innerWidth < 768;
        var map = L.map(el, { zoomControl: !isMobileView, attributionControl: false }).setView([lat, lon], zoom);
        el._leafletMap = map;

        // Base tile layer (theme-aware)
        var baseLayer = L.tileLayer(getBaseTileUrl(), { maxZoom: 18 });
        baseLayer.addTo(map);
        el._radarBaseLayer = baseLayer;

        // Attribution
        var attr = L.control.attribution({ position: 'bottomright', prefix: false });
        attr.addAttribution('&copy; <a href="https://www.openstreetmap.org/copyright">OSM</a>');
        attr.addTo(map);

        // Radar overlay
        function addRadarLayer(url) {
            if (el._radarLayer) { map.removeLayer(el._radarLayer); el._radarLayer = null; }
            el._radarLayer = L.tileLayer(url, { opacity: opacity, maxZoom: 18 });
            el._radarLayer.addTo(map);
        }

        if (customTile) {
            addRadarLayer(customTile);
        } else {
            // RainViewer: fetch latest frame timestamp
            fetch('https://api.rainviewer.com/public/weather-maps.json')
                .then(function (r) { return r.json(); })
                .then(function (rv) {
                    var past = rv && rv.radar && rv.radar.past;
                    if (!past || !past.length) return;
                    var latest = past[past.length - 1];
                    var rvUrl = rv.host + latest.path + '/256/{z}/{x}/{y}/2/1_1.png';
                    addRadarLayer(rvUrl);
                    attr.addAttribution('<a href="https://www.rainviewer.com/api.html" target="_blank">RainViewer</a>');
                })
                .catch(function (e) { console.warn('frcastr: RainViewer fetch failed', e); });
        }

        // Swap base tile when Bootstrap theme changes
        var htmlRoot = document.getElementById('htmlRoot');
        if (htmlRoot) {
            var themeObserver = new MutationObserver(function () {
                var newUrl = getBaseTileUrl();
                if (el._radarBaseLayer) {
                    map.removeLayer(el._radarBaseLayer);
                    el._radarBaseLayer = L.tileLayer(newUrl, { maxZoom: 18 });
                    el._radarBaseLayer.addTo(map);
                    el._radarBaseLayer.bringToBack();
                }
            });
            themeObserver.observe(htmlRoot, { attributes: true, attributeFilter: ['data-bs-theme'] });
            el._themeObserver = themeObserver;
        }

        // Disable GridStack drag while the pointer is inside the map so Leaflet pan works freely
        var gsItem = el.closest('.grid-stack-item');
        function disableWidgetDrag(e) {
            if (e.buttons !== 0) return; // don't interrupt an in-progress drag from the titlebar
            if (gsItem && window.frcastrGrid) window.frcastrGrid.update(gsItem, { noMove: true });
        }
        function enableWidgetDrag() {
            if (gsItem && window.frcastrGrid) window.frcastrGrid.update(gsItem, { noMove: false });
        }
        el.addEventListener('pointerenter', disableWidgetDrag);
        el.addEventListener('pointerleave', enableWidgetDrag);

        // Save zoom/pan back to widget config when user moves the map
        var widgetEl = el.closest('[data-widget-id]');
        var wId = widgetEl ? parseInt(widgetEl.dataset.widgetId, 10) : NaN;
        if (!isNaN(wId) && window.frcastrSaveWidgetConfig) {
            var mapSaveTimer;
            map.on('moveend zoomend', function () {
                clearTimeout(mapSaveTimer);
                mapSaveTimer = setTimeout(function () {
                    var c = map.getCenter();
                    window.frcastrSaveWidgetConfig(wId, { lat: c.lat, lon: c.lng, zoom: map.getZoom() });
                }, 800);
            });
        }

        // Clean up when grid removes/replaces the element
        map.on('remove', function () {
            if (el._themeObserver) { el._themeObserver.disconnect(); el._themeObserver = null; }
            el.removeEventListener('pointerenter', disableWidgetDrag);
            el.removeEventListener('pointerleave', enableWidgetDrag);
            enableWidgetDrag(); // restore movability in case pointer was inside when removed
        });
    };

    // 18: Hourly Forecast
    R[18] = function (el, config, data) {
        var periods = (data && data.forecast && data.forecast.aggregatedHourly) || [];
        var max  = Number(config.periods) || 12;
        var u    = window.getTempUnit ? window.getTempUnit() : (config.unit || 'C');

        // Fit as many hours as the container width allows, up to configured max
        var availWidth = el.getBoundingClientRect().width || 300;
        var maxFit = Math.max(1, Math.floor(availWidth / 64));
        var list = periods.slice(0, Math.min(max, maxFit));

        if (!list.length) {
            el.innerHTML = '<div class="d-flex align-items-center justify-content-center h-100 text-body-secondary small">No hourly forecast data</div>';
            return;
        }

        var use12h = config.format !== '24h';
        var html = '<div class="d-flex h-100 align-items-center overflow-hidden">';
        list.forEach(function (p) {
            var d    = new Date(utcTs(p.periodStart) || p.periodStart);
            var hour = use12h
                ? (function () { var h = d.getHours(); var ampm = h >= 12 ? 'PM' : 'AM'; h = h % 12 || 12; return h + ampm; })()
                : pad(d.getHours()) + ':00';
            var icon = conditionIcon(p.condition);
            var temp = p.temperature != null
                ? fmt(u === 'F' ? toF(p.temperature) : p.temperature, 0) + '&deg;' + u
                : '&ndash;';
            var prcp = p.precipChance != null ? Math.round(p.precipChance) + '%' : '';
            html +=
                '<div class="d-flex flex-column align-items-center gap-1" style="flex:1 1 0;min-width:0;overflow:hidden">' +
                '<div class="text-body-secondary text-truncate w-100 text-center" style="font-size:clamp(0.75rem,3cqmin,1rem)">' + escHtml(hour) + '</div>' +
                '<div style="font-size:clamp(1.3rem,6cqmin,3rem);line-height:1">' + icon + '</div>' +
                '<div class="fw-semibold" style="font-size:clamp(0.875rem,3.5cqmin,1.4rem)">' + temp + '</div>' +
                (prcp ? '<div class="text-info" style="font-size:clamp(0.7rem,2.8cqmin,1rem)">' + prcp + '</div>' : '') +
                '</div>';
        });
        html += '</div>';
        el.innerHTML = html;
    };

    // ── Public API ────────────────────────────────────────────────────────────

    return {
        render: function (type, el, config, data) {
            var renderer = R[type];
            if (!renderer) {
                el.innerHTML = '<div class="d-flex align-items-center justify-content-center h-100 text-body-secondary small">Widget type ' + type + '</div>';
                return;
            }
            try {
                renderer(el, config || {}, data || {});
            } catch (e) {
                el.innerHTML = '<div class="d-flex align-items-center justify-content-center h-100 text-danger small">Render error</div>';
                console.error('WeatherWidgets.render type=' + type, e);
            }
        }
    };
})();
