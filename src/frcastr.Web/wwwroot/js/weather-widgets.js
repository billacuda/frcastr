window.WeatherWidgets = (function () {
    'use strict';

    var cfg = window.frcastrConfig || {};
    var pollSecs = cfg.pollIntervalSeconds || 30;
    var offlineSecs = (cfg.sensorOfflineThresholdMinutes || 10) * 60;

    // ── Helpers ──────────────────────────────────────────────────────────────

    function reading(data, channel) {
        return (data && data.current && data.current.readings && data.current.readings[channel]) || null;
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
        // Append Z so JS Date treats it as UTC, not local
        return isoTs.endsWith('Z') ? isoTs : isoTs + 'Z';
    }

    function ageMs(isoTs) {
        var t = utcTs(isoTs);
        if (!t) return Infinity;
        return Date.now() - new Date(t).getTime();
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
        if (ms === Infinity) return 'text-body-secondary';
        var secs = ms / 1000;
        if (secs > offlineSecs)  return 'text-danger';
        if (secs > pollSecs * 2) return 'text-warning';
        return 'text-body-secondary';
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
        if (v <=  50) return { label: 'Good',         cls: 'success' };
        if (v <= 100) return { label: 'Moderate',      cls: 'warning' };
        if (v <= 150) return { label: 'USG',           cls: 'warning' };
        if (v <= 200) return { label: 'Unhealthy',     cls: 'danger'  };
        if (v <= 300) return { label: 'Very Unhealthy',cls: 'danger'  };
        return             { label: 'Hazardous',      cls: 'danger'  };
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
        if (direction === 'Rising')  return '<span class="text-danger ms-1" title="Rising">&#8593;</span>';
        if (direction === 'Falling') return '<span class="text-info ms-1" title="Falling">&#8595;</span>';
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
        var precip   = Number((readings['rainfall']            || {}).value || 0);
        var temp     = val({ value: (readings['temperature.outdoor'] || {}).value || 15 });
        var hasLightning = readings['lightning'] != null;
        var wind     = Number((readings['wind.speed'] || {}).value || 0);
        if (hasLightning)         return 'wa-lightning';
        if (precip > 0 && temp <= 2) return 'wa-snow';
        if (precip > 0)           return 'wa-rain';
        if (wind > 50)            return 'wa-cloud';
        return 'wa-sun';
    }

    function buildAnimHtml(cls) {
        if (cls === 'wa-sun') {
            return '<div class="weather-anim ' + cls + '"><div class="wa-sun-disc"></div></div>';
        }
        if (cls === 'wa-cloud') {
            return '<div class="weather-anim ' + cls + '">' +
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
        return '<div class="weather-anim ' + cls + '">' +
            '<div class="wa-cloud-shape"></div>' +
            (cls === 'wa-lightning'
                ? '<div class="wa-bolt">&#9889;</div>'
                : '<div class="wa-drops">' + drops + '</div>') +
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
                '<div class="d-flex flex-column align-items-center justify-content-center h-100 text-center gap-1">' +
                '<div class="display-6 fw-bold lh-1" data-clock></div>' +
                (showDate ? '<div class="small text-body-secondary" data-caldate></div>' : '') +
                '</div>';
        }
        var clockEl = el.querySelector('[data-clock]');
        var dateEl  = el.querySelector('[data-caldate]');
        if (clockEl) clockEl.textContent = timeStr;
        if (dateEl)  dateEl.textContent  = dateStr;
    };

    // 1: Temperature Outdoor
    R[1] = function (el, config, data) {
        var ch  = config.channel || 'temperature.outdoor';
        var r   = reading(data, ch);
        var v   = val(r);
        var u   = window.getTempUnit ? window.getTempUnit() : (config.unit || 'C');
        var dv  = v == null ? null : (u === 'F' ? toF(v) : v);
        var trend = (data && data.current && data.current.trends && data.current.trends[ch]) || null;
        el.innerHTML =
            '<div class="d-flex flex-column h-100">' +
            '<div class="d-flex align-items-baseline gap-1">' +
            '<span class="display-5 fw-bold">' + fmt(dv) + '</span>' +
            '<span class="fs-4 text-body-secondary">&deg;' + u + '</span>' +
            (trend ? trendArrow(trend.direction) : '') +
            '</div>' +
            tsHtml(ts(r)) +
            '</div>';
    };

    // 2: Temperature Indoor
    R[2] = function (el, config, data) {
        var ch = config.channel || 'temperature.indoor';
        var r  = reading(data, ch);
        var v  = val(r);
        var u  = window.getTempUnit ? window.getTempUnit() : (config.unit || 'C');
        var dv = v == null ? null : (u === 'F' ? toF(v) : v);
        el.innerHTML =
            '<div class="d-flex flex-column h-100">' +
            '<div class="d-flex align-items-baseline gap-1">' +
            '<span class="display-5 fw-bold">' + fmt(dv) + '</span>' +
            '<span class="fs-4 text-body-secondary">&deg;' + u + '</span>' +
            '</div>' +
            tsHtml(ts(r)) +
            '</div>';
    };

    // 3: Humidity Outdoor
    R[3] = function (el, config, data) {
        var ch     = config.channel || 'humidity.outdoor';
        var r      = reading(data, ch);
        var v      = val(r);
        var showDp = config.showDewpoint !== false;
        var dpR    = showDp ? reading(data, 'dewpoint.outdoor') : null;
        var dpV    = val(dpR);
        el.innerHTML =
            '<div class="d-flex flex-column h-100">' +
            '<div class="d-flex align-items-baseline gap-1">' +
            '<span class="display-5 fw-bold">' + fmt(v, 0) + '</span>' +
            '<span class="fs-4 text-body-secondary">%</span>' +
            '</div>' +
            (dpV != null ? '<div class="small text-body-secondary">Dew point: ' + fmt(dpV) + '&deg;C</div>' : '') +
            tsHtml(ts(r)) +
            '</div>';
    };

    // 4: Humidity Indoor
    R[4] = function (el, config, data) {
        var ch  = config.channel || 'humidity.indoor';
        var r   = reading(data, ch);
        var v   = val(r);
        var dpR = reading(data, 'dewpoint.indoor');
        var dpV = val(dpR);
        el.innerHTML =
            '<div class="d-flex flex-column h-100">' +
            '<div class="d-flex align-items-baseline gap-1">' +
            '<span class="display-5 fw-bold">' + fmt(v, 0) + '</span>' +
            '<span class="fs-4 text-body-secondary">%</span>' +
            '</div>' +
            (dpV != null ? '<div class="small text-body-secondary">Dew point: ' + fmt(dpV) + '&deg;C</div>' : '') +
            tsHtml(ts(r)) +
            '</div>';
    };

    // 5: Pressure
    R[5] = function (el, config, data) {
        var ch  = config.channel || 'pressure';
        var r   = reading(data, ch);
        var v   = val(r);
        var u   = config.unit || 'hPa';
        var dv  = v, ul = 'hPa', dec = 0;
        if (v != null && u === 'inHg') { dv = toInHg(v); ul = 'inHg'; dec = 2; }
        else if (v != null && u === 'mmHg') { dv = toMmHg(v); ul = 'mmHg'; dec = 0; }
        el.innerHTML =
            '<div class="d-flex flex-column h-100">' +
            '<div class="d-flex align-items-baseline gap-1">' +
            '<span class="display-5 fw-bold">' + fmt(dv, dec) + '</span>' +
            '<span class="fs-6 text-body-secondary">' + ul + '</span>' +
            '</div>' +
            tsHtml(ts(r)) +
            '</div>';
    };

    // 6: Wind
    R[6] = function (el, config, data) {
        var sCh = config.speedChannel     || 'wind.speed';
        var dCh = config.directionChannel || 'wind.direction';
        var sR  = reading(data, sCh);
        var dR  = reading(data, dCh);
        var sv  = val(sR), dv = val(dR);
        var u   = config.speedUnit || 'kmh';
        var ds  = sv, ul = 'km/h';
        if (sv != null && u === 'mph')   { ds = toMph(sv);   ul = 'mph'; }
        else if (sv != null && u === 'knots') { ds = toKnots(sv); ul = 'kn';  }
        else if (sv != null && u === 'ms')    { ds = sv / 3.6;    ul = 'm/s'; }
        var dirLabel  = windDirLabel(dv);
        var rotateCss = dv != null
            ? 'transform:rotate(' + dv + 'deg);'
            : 'visibility:hidden;';
        el.innerHTML =
            '<div class="d-flex flex-column h-100">' +
            '<div class="d-flex align-items-center gap-3">' +
            '<div style="width:44px;height:44px;position:relative;border:2px solid var(--bs-border-color);border-radius:50%;flex-shrink:0">' +
            '<div style="position:absolute;top:3px;left:50%;transform:translateX(-50%);font-size:9px;line-height:1">N</div>' +
            '<div style="position:absolute;top:50%;left:50%;width:2px;height:44%;background:var(--bs-primary);transform-origin:bottom center;margin-left:-1px;' + rotateCss + 'margin-top:-44%"></div>' +
            '</div>' +
            '<div>' +
            '<div class="d-flex align-items-baseline gap-1">' +
            '<span class="fs-2 fw-bold">' + fmt(ds) + '</span>' +
            '<span class="small text-body-secondary">' + ul + '</span>' +
            '</div>' +
            '<div class="small text-body-secondary">' + dirLabel + '</div>' +
            '</div></div>' +
            tsHtml(ts(sR)) +
            '</div>';
    };

    // 7: Weather Animation
    R[7] = function (el, config, data) {
        var readings = (data && data.current && data.current.readings) || {};
        var cls = animClass(readings, config);
        el.innerHTML = buildAnimHtml(cls);
    };

    // 8: Forecast
    R[8] = function (el, config, data) {
        var periods = (data && data.forecast && data.forecast.aggregated) || [];
        var max  = Number(config.periods) || 5;
        var u    = window.getTempUnit ? window.getTempUnit() : (config.unit || 'C');
        var list = periods.slice(0, max);

        if (!list.length) {
            el.innerHTML = '<div class="d-flex align-items-center justify-content-center h-100 text-body-secondary small">No forecast data</div>';
            return;
        }

        var html = '<div class="d-flex flex-wrap gap-2 h-100 align-items-start overflow-hidden">';
        list.forEach(function (p) {
            var d    = new Date(utcTs(p.periodStart) || p.periodStart);
            var day  = d.toLocaleDateString(undefined, { weekday: 'short' });
            var icon = conditionIcon(p.condition);
            var temp = p.temperature != null
                ? fmt(u === 'F' ? toF(p.temperature) : p.temperature, 0) + '&deg;' + u
                : '&ndash;';
            var prcp = p.precipChance != null ? Math.round(p.precipChance) + '%' : '';
            html +=
                '<div class="d-flex flex-column align-items-center gap-1" style="min-width:50px">' +
                '<div class="x-small text-body-secondary">' + escHtml(day) + '</div>' +
                '<div style="font-size:1.4rem;line-height:1">' + icon + '</div>' +
                '<div class="small fw-semibold">' + temp + '</div>' +
                (prcp ? '<div class="x-small text-info">' + prcp + '</div>' : '') +
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
        var illum     = moon.illumination != null ? Math.round(moon.illumination * 100) + '%' : '';
        el.innerHTML =
            '<div class="d-flex flex-column align-items-center justify-content-center h-100 text-center gap-1">' +
            '<div style="font-size:3rem;line-height:1">' + escHtml(moon.icon || '🌙') + '</div>' +
            (showName && moon.phaseName ? '<div class="fw-semibold small">' + escHtml(moon.phaseName) + '</div>' : '') +
            (showIllum && illum ? '<div class="small text-body-secondary">' + illum + ' lit</div>' : '') +
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
        if (showL && sun.dayLength) {
            var dl = String(sun.dayLength);
            var parts = dl.split(':');
            var dlStr = parts.length >= 2 ? parts[0] + 'h ' + parts[1] + 'm' : dl;
            rows.push(['&#9203; Day length', dlStr]);
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
        var r  = reading(data, 'feelslike.outdoor');
        var v  = val(r);
        var u  = window.getTempUnit ? window.getTempUnit() : (config.unit || 'C');
        var dv = v == null ? null : (u === 'F' ? toF(v) : v);
        el.innerHTML =
            '<div class="d-flex flex-column h-100">' +
            '<div class="d-flex align-items-baseline gap-1">' +
            '<span class="display-5 fw-bold">' + fmt(dv) + '</span>' +
            '<span class="fs-4 text-body-secondary">&deg;' + u + '</span>' +
            '</div>' +
            tsHtml(ts(r)) +
            '</div>';
    };

    // 14: Rainfall
    R[14] = function (el, config, data) {
        var ch  = config.channel || 'rainfall';
        var r   = reading(data, ch);
        var v   = val(r);
        var u   = config.unit || 'mm';
        var dv  = v, ul = 'mm';
        if (v != null && u === 'in') { dv = toIn(v); ul = 'in'; }
        el.innerHTML =
            '<div class="d-flex flex-column h-100">' +
            '<div class="d-flex align-items-baseline gap-1">' +
            '<span class="display-5 fw-bold">' + fmt(dv) + '</span>' +
            '<span class="fs-5 text-body-secondary">' + ul + '</span>' +
            '</div>' +
            tsHtml(ts(r)) +
            '</div>';
    };

    // 15: Pressure Trend (sparkline)
    R[15] = function (el, config, data) {
        var ch  = config.channel || 'pressure';
        var r   = reading(data, ch);
        var v   = val(r);
        var u   = config.unit || 'hPa';
        var dv  = v, ul = 'hPa', dec = 0;
        if (v != null && u === 'inHg') { dv = toInHg(v); ul = 'inHg'; dec = 2; }
        else if (v != null && u === 'mmHg') { dv = toMmHg(v); ul = 'mmHg'; dec = 0; }

        var wEl    = el.closest('[data-widget-id]');
        var wId    = (wEl && wEl.dataset.widgetId) || 'x';
        var cvsId  = 'chart-press-' + wId;

        if (!el.querySelector('[data-press-val]')) {
            el.innerHTML =
                '<div class="d-flex align-items-baseline gap-1 mb-1">' +
                '<span class="fs-2 fw-bold" data-press-val></span>' +
                '<span class="small text-body-secondary">' + ul + '</span>' +
                '</div>' +
                '<div class="flex-grow-1" style="min-height:50px;position:relative">' +
                '<canvas id="' + cvsId + '"></canvas>' +
                '</div>' +
                '<div class="mt-1 small" data-press-ts></div>';
        }

        var valEl = el.querySelector('[data-press-val]');
        var tsEl  = el.querySelector('[data-press-ts]');
        if (valEl) valEl.textContent = fmt(dv, dec);
        if (tsEl) {
            tsEl.textContent  = ageStr(ts(r));
            tsEl.className    = 'mt-1 small ' + ageClass(ts(r));
        }

        var history = (data && data.history && data.history[ch]) || [];
        var points  = history.map(function (h) {
            if (u === 'inHg')  return toInHg(h.v);
            if (u === 'mmHg')  return toMmHg(h.v);
            return h.v;
        });
        var canvas = document.getElementById(cvsId);
        sparkline(canvas, points, 'rgb(99,102,241)');
    };

    // 16: Air Quality
    R[16] = function (el, config, data) {
        var ch   = config.channel || 'aqi.outdoor';
        var r    = reading(data, ch);
        var v    = val(r);
        var cat  = v != null ? aqiCategory(Math.round(v)) : null;
        var show = config.showCategory !== false;
        el.innerHTML =
            '<div class="d-flex flex-column h-100">' +
            '<div class="d-flex align-items-baseline gap-1">' +
            '<span class="display-5 fw-bold">' + fmt(v, 0) + '</span>' +
            '<span class="fs-6 text-body-secondary">AQI</span>' +
            '</div>' +
            (cat && show ? '<div class="badge text-bg-' + cat.cls + ' align-self-start mt-1">' + cat.label + '</div>' : '') +
            tsHtml(ts(r)) +
            '</div>';
    };

    // 17: Radar
    R[17] = function (el, config, data) {
        var imgUrl = config.imageUrl || '';
        if (!imgUrl) {
            el.innerHTML = '<div class="d-flex align-items-center justify-content-center h-100 text-body-secondary small">No radar URL configured</div>';
            return;
        }
        var refreshSecs = Number(config.refreshSeconds) || 300;
        var cacheBust   = Math.floor(Date.now() / (refreshSecs * 1000));
        var sep         = imgUrl.indexOf('?') >= 0 ? '&' : '?';
        var src         = imgUrl + sep + '_t=' + cacheBust;
        el.innerHTML = '<img src="' + escHtml(src) + '" alt="' + escHtml(config.altText || 'Radar') +
            '" class="img-fluid w-100 h-100" style="object-fit:contain" />';
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
