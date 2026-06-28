(function () {
    'use strict';

    var cfg          = window.frcastrConfig || {};
    var pollMs       = (cfg.pollIntervalSeconds || 30) * 1000;
    var isMobile     = window.innerWidth < 768;
    var LAYOUT_KEY   = 'frcastr-layout';
    var LAST_DASH_KEY = 'frcastr-last-dash';
    var MAX_HISTORY  = 60;

    var urlDash     = (new URLSearchParams(window.location.search)).get('dash');
    var currentDash = urlDash || localStorage.getItem(LAST_DASH_KEY) || 'Default';
    localStorage.setItem(LAST_DASH_KEY, currentDash);
    if (!urlDash && currentDash !== 'Default') {
        history.replaceState(null, '', '/?dash=' + encodeURIComponent(currentDash));
    }

    var grid          = null;
    var allDefs       = [];
    var latestData    = {};
    var channelHistory = {};
    var saveTimer     = null;
    var editMode      = false;
    var resizeTimer   = null;
    var desktopRefRow = 0; // max row of saved desktop layout; used as refRow on mobile

    // ── Cell height ───────────────────────────────────────────────────────────

    function computeCellHeight(maxRows) {
        if (!maxRows || maxRows < 1) return 20;
        var wrapper = document.getElementById('dashboardWrapper');
        var topOffset = wrapper ? wrapper.getBoundingClientRect().top : 64;
        var margin = 6;
        var available = window.innerHeight - topOffset - (maxRows + 1) * margin;
        return Math.max(Math.floor(available / maxRows / 2), 5);
    }

    function maxRowFromDefs(defs) {
        var max = 0;
        defs.forEach(function (d) { var bottom = (d.gridY || 0) + (d.gridH || 3); if (bottom > max) max = bottom; });
        return max;
    }

    function updateCellHeight() {
        if (!grid) return;
        var nodes = grid.save(false) || [];
        var maxRow = 0;
        nodes.forEach(function (n) { var b = (n.y || 0) + (n.h || 1); if (b > maxRow) maxRow = b; });
        if (!maxRow) maxRow = desktopRefRow || maxRowFromDefs(allDefs);
        // On mobile, widgets stack vertically so maxRow is much larger than on desktop.
        // Use the saved desktop layout's row count so both platforms compute the same
        // cell height relative to their viewport — widgets look proportionally identical.
        var refRow = isMobile ? Math.max(desktopRefRow || maxRowFromDefs(allDefs), 1) : maxRow;
        grid.cellHeight(computeCellHeight(refRow));
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    async function init() {
        if (cfg.kioskMode) activateKiosk();

        var layoutResp = {}, widgetDefs = [];
        try {
            var dashParam = '?dashboard=' + encodeURIComponent(currentDash);
        var results = await Promise.all([
                fetch('/api/dashboard/layout'  + dashParam).then(function (r) { return r.json(); }),
                fetch('/api/dashboard/widgets' + dashParam).then(function (r) { return r.json(); })
            ]);
            layoutResp = results[0] || {};
            widgetDefs = results[1] || [];
        } catch (e) {
            console.warn('frcastr: could not load dashboard config', e);
        }

        allDefs = Array.isArray(widgetDefs) ? widgetDefs : [];

        if (!allDefs.length) {
            var container = document.getElementById('dashboard-grid');
            if (container) {
                container.innerHTML =
                    '<div class="d-flex align-items-center justify-content-center py-5 text-body-secondary">' +
                    'No widgets configured. Log in and visit Admin &rsaquo; Widgets to add widgets.' +
                    '</div>';
            }
            return;
        }

        var initCellH = 20; // placeholder; updateCellHeight() sets the real value below

        grid = GridStack.init({
            column:        isMobile ? 2 : 12,
            cellHeight:    initCellH,
            margin:        6,
            minH:          1,
            handle:        '.widget-titlebar',
            disableResize: isMobile || !cfg.isAuthenticated,
            disableDrag:   isMobile || !cfg.isAuthenticated,
            animate:       true
        }, '#dashboard-grid');

        if (cfg.isAuthenticated && !isMobile) {
            var gridEl = document.getElementById('dashboard-grid');
            if (gridEl) gridEl.classList.add('gs-editable');
        }

        // Expose grid for widgets that need to temporarily disable drag (e.g. Radar)
        window.frcastrGrid = grid;

        allDefs.forEach(function (w) {
            var itemEl = grid.addWidget({
                id:       String(w.id),
                x:        w.gridX || 0,
                y:        w.gridY || 0,
                w:        isMobile ? 2 : (w.gridW || 4),
                h:        w.gridH || 2,
                minH:     1,
                noResize: isMobile,
                noMove:   isMobile
            });
            if (itemEl) {
                var contentEl = itemEl.querySelector('.grid-stack-item-content');
                if (contentEl) contentEl.innerHTML = buildShell(w);
            }
        });

        // Apply saved layout (server preferred, localStorage fallback)
        var savedRaw   = isMobile ? layoutResp.mobile   : layoutResp.desktop;
        var savedLocal = getLocalLayout(isMobile ? 'mobile' : 'desktop');
        var saved      = parseLayout(savedRaw) || savedLocal;
        if (saved && saved.length) {
            grid.load(saved, false);
        }

        // Compute desktop reference row count once. On mobile, widgets stack into many
        // more rows than on desktop, so we calibrate cell height using the desktop
        // layout's row span so both platforms look proportionally identical.
        var desktopLayout = parseLayout(layoutResp.desktop) || getLocalLayout('desktop');
        if (desktopLayout && desktopLayout.length) {
            desktopLayout.forEach(function (n) {
                var b = (n.y || 0) + (n.h || 1);
                if (b > desktopRefRow) desktopRefRow = b;
            });
        }
        if (!desktopRefRow) desktopRefRow = maxRowFromDefs(allDefs);

        // Set cell height after layout is applied — same formula on mobile and desktop
        updateCellHeight();

        // Wire change handler after a tick so initial load doesn't trigger a save
        setTimeout(function () {
            grid.on('change', function () {
                clearTimeout(saveTimer);
                saveTimer = setTimeout(saveLayout, 600);
            });
            // Re-render the specific widget after user resizes it
            grid.on('resizestop', function (event, el) {
                var inner = el.querySelector('[data-widget-id]');
                if (inner) renderWidget(inner);
            });
        }, 300);

        // Recalculate cell height on window resize
        window.addEventListener('resize', function () {
            clearTimeout(resizeTimer);
            resizeTimer = setTimeout(function () {
                updateCellHeight();
                renderAll();
            }, 150);
        });

        await refreshData();
        setInterval(refreshData, pollMs);
        setInterval(tickClocks, 1000);

        window.addEventListener('tempUnitChanged', function () { renderAll(); });
    }

    // ── Widget shell HTML ─────────────────────────────────────────────────────

    function buildShell(w) {
        var btnHtml = cfg.isAdmin
            ? '<button class="widget-action-btn" title="Edit widget" onclick="openEditWidgetModal(' + w.id + ')">&#x270F;</button>' +
              '<button class="widget-action-btn" title="Remove widget" onclick="confirmRemoveWidget(' + w.id + ')">&#x2715;</button>'
            : '';
        return '<div class="widget-inner h-100 d-flex flex-column" data-widget-id="' + w.id + '" data-widget-type="' + w.type + '">' +
            '<div class="widget-titlebar d-flex align-items-center px-2 py-1">' +
            '<span class="widget-title small fw-semibold text-body-secondary flex-grow-1 text-truncate">' + escHtml(w.title) + '</span>' +
            btnHtml +
            '</div>' +
            '<div class="widget-body flex-grow-1 d-flex flex-column px-2 pb-2 overflow-hidden" data-widget-body></div>' +
            '</div>';
    }

    // ── Data refresh ──────────────────────────────────────────────────────────

    async function refreshData() {
        try {
            var results = await Promise.all([
                fetch('/api/weather/current').then(function (r)        { return r.json(); }).catch(function () { return null; }),
                fetch('/api/weather/forecast').then(function (r)       { return r.json(); }).catch(function () { return null; }),
                fetch('/api/weather/moon').then(function (r)           { return r.json(); }).catch(function () { return null; }),
                fetch('/api/weather/sun').then(function (r)            { return r.json(); }).catch(function () { return null; }),
                fetch('/api/weather/alerts').then(function (r)         { return r.json(); }).catch(function () { return [];   }),
                fetch('/api/weather/daily-extremes').then(function (r) { return r.json(); }).catch(function () { return null; })
            ]);

            var current        = results[0];
            var forecast       = results[1];
            var moon           = results[2];
            var sun            = results[3];
            var alerts         = results[4];
            var dailyExtremes  = results[5];

            if (current && current.readings) {
                bufferReadings(current.readings);
            }

            latestData = { current: current, forecast: forecast, moon: moon, sun: sun, alerts: alerts, history: channelHistory, dailyExtremes: dailyExtremes };

            renderAll();
            applyStaleFlags((current && current.staleChannels) || []);
        } catch (e) {
            console.warn('frcastr: poll error', e);
        }
    }

    function bufferReadings(readings) {
        var now = Date.now();
        Object.keys(readings).forEach(function (ch) {
            var r = readings[ch];
            if (!channelHistory[ch]) channelHistory[ch] = [];
            var buf = channelHistory[ch];
            buf.push({ t: now, v: Number(r.value) });
            if (buf.length > MAX_HISTORY) buf.shift();
        });
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    function renderAll() {
        document.querySelectorAll('[data-widget-id]').forEach(function (el) {
            renderWidget(el);
        });
    }

    function tickClocks() {
        document.querySelectorAll('[data-widget-type="0"] [data-widget-body]').forEach(function (body) {
            var wEl  = body.closest('[data-widget-id]');
            var wId  = wEl && parseInt(wEl.dataset.widgetId, 10);
            var def  = allDefs.find(function (d) { return d.id === wId; });
            var conf = safeJson(def && def.config);
            if (window.WeatherWidgets) {
                WeatherWidgets.render(0, body, conf, latestData);
            }
        });
    }

    function renderWidget(el) {
        var wId   = parseInt(el.dataset.widgetId, 10);
        var wType = parseInt(el.dataset.widgetType, 10);
        var def   = allDefs.find(function (d) { return d.id === wId; });
        var conf  = safeJson(def && def.config);
        applyWidgetColor(el, conf.color);
        var body  = el.querySelector('[data-widget-body]');
        if (!body || !window.WeatherWidgets) return;
        WeatherWidgets.render(wType, body, conf, latestData);
    }

    // Per-widget text color: drives the --widget-color CSS variable used by site.css.
    function applyWidgetColor(el, color) {
        if (color) {
            el.style.setProperty('--widget-color', color);
            el.classList.add('has-widget-color');
        } else {
            el.style.removeProperty('--widget-color');
            el.classList.remove('has-widget-color');
        }
    }

    // ── Stale badges ──────────────────────────────────────────────────────────

    function applyStaleFlags(staleChannels) {
        var staleSet = {};
        staleChannels.forEach(function (ch) { staleSet[ch] = true; });

        document.querySelectorAll('[data-widget-id]').forEach(function (el) {
            var wId   = parseInt(el.dataset.widgetId, 10);
            var wType = parseInt(el.dataset.widgetType, 10);
            var def   = allDefs.find(function (d) { return d.id === wId; });
            var conf  = safeJson(def && def.config);
            var chans = widgetChannels(wType, conf);
            var stale = chans.some(function (ch) { return staleSet[ch]; });

            var badge = el.querySelector('.stale-badge');
            if (stale) {
                el.classList.add('widget-stale');
                if (!badge) {
                    badge = document.createElement('span');
                    badge.className = 'stale-badge badge text-bg-warning position-absolute top-0 end-0 m-1';
                    badge.textContent = '⚠';
                    badge.title = 'Data may be stale';
                    el.style.position = 'relative';
                    el.appendChild(badge);
                }
            } else {
                el.classList.remove('widget-stale');
                if (badge) badge.remove();
            }
        });
    }

    function widgetChannels(type, config) {
        switch (type) {
            case 1:  return [config.channel || 'temperature.outdoor'];
            case 2:  return [config.channel || 'temperature.indoor'];
            case 3:  return [config.channel || 'humidity.outdoor'];
            case 4:  return [config.channel || 'humidity.indoor'];
            case 5:  return [config.channel || 'pressure'];
            case 6:  return [config.speedChannel || 'wind.speed', config.directionChannel || 'wind.direction'];
            case 10: return config.channel ? [config.channel] : [];
            case 13: return ['feelslike.outdoor'];
            case 14: return [config.channel || 'rainfall'];
            case 15: return [config.channel || 'pressure'];
            case 16: return [config.channel || 'aqi.outdoor'];
            default: return [];
        }
    }

    // ── Layout save ───────────────────────────────────────────────────────────

    async function saveLayout() {
        if (!grid) return;
        var items = grid.save(false);
        var layout = items.map(function (n) {
            return { id: n.id, x: n.x, y: n.y, w: n.w, h: n.h };
        });
        var layoutJson = JSON.stringify(layout);

        if (!cfg.isAuthenticated) {
            setLocalLayout(isMobile ? 'mobile' : 'desktop', layout);
            return;
        }

        try {
            var body = JSON.stringify(
                isMobile
                    ? { desktop: null, mobile: layoutJson }
                    : { desktop: layoutJson, mobile: null }
            );
            var resp = await fetch('/api/dashboard/layout?dashboard=' + encodeURIComponent(currentDash), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: body
            });
            if (resp.status === 401) {
                setLocalLayout(isMobile ? 'mobile' : 'desktop', layout);
            }
        } catch (e) {
            setLocalLayout(isMobile ? 'mobile' : 'desktop', layout);
        }
    }

    // ── Widget config save (used by radar widget to persist zoom/pan) ─────────

    window.frcastrSaveWidgetConfig = async function (id, patch) {
        var def = allDefs.find(function (d) { return d.id === id; });
        if (!def) return;
        var merged = Object.assign({}, safeJson(def.config), patch);
        def.config = JSON.stringify(merged);
        if (!cfg.isAuthenticated) return;
        try {
            await fetch('/api/dashboard/widgets/' + id + '/config', {
                method:  'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body:    JSON.stringify(merged)
            });
        } catch (e) {
            console.warn('frcastr: widget config save failed', e);
        }
    };

    // ── Kiosk mode ────────────────────────────────────────────────────────────

    function activateKiosk() {
        var nav = document.getElementById('mainNav');
        if (nav) nav.style.display = 'none';
        var meta = document.createElement('meta');
        meta.httpEquiv = 'refresh';
        meta.content = '3600';
        document.head.appendChild(meta);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    function parseLayout(raw) {
        if (!raw) return null;
        try {
            var v = typeof raw === 'string' ? JSON.parse(raw) : raw;
            return Array.isArray(v) ? v : null;
        } catch (e) { return null; }
    }

    function getLocalLayout(key) {
        try { return JSON.parse(localStorage.getItem(LAYOUT_KEY + '-' + key)); } catch (e) { return null; }
    }

    function setLocalLayout(key, layout) {
        try { localStorage.setItem(LAYOUT_KEY + '-' + key, JSON.stringify(layout)); } catch (e) {}
    }

    function safeJson(s) {
        if (!s) return {};
        try { return JSON.parse(s); } catch (e) { return {}; }
    }

    function escHtml(s) {
        return String(s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
    }

    // ── Dashboard edit mode ───────────────────────────────────────────────────

    window.toggleDashboardEdit = function () {
        editMode = !editMode;
        var btn    = document.getElementById('editDashBtn');
        var addBtn = document.getElementById('addWidgetNavItem');
        if (btn)    btn.textContent = editMode ? 'Done' : 'Edit';
        if (addBtn) addBtn.style.display = editMode ? '' : 'none';
    };

    window.confirmRemoveWidget = async function (id) {
        var def  = allDefs.find(function (d) { return d.id === id; });
        var name = def ? def.title : 'this widget';
        if (!confirm('Remove "' + escHtml(name) + '" from this dashboard?')) return;

        var r = await fetch('/api/admin/widgets/' + id, { method: 'DELETE' });
        if (!r.ok) { alert('Could not remove widget.'); return; }

        var inner = document.querySelector('[data-widget-id="' + id + '"]');
        if (inner) {
            var item = inner.closest('.grid-stack-item');
            if (item && grid) grid.removeWidget(item);
        }
        allDefs = allDefs.filter(function (d) { return d.id !== id; });
    };

    // ── Channel picker helpers ────────────────────────────────────────────────

    var cachedChannels = null;
    var ewModeIsSimple = true;
    var awModeIsSimple = true;

    var SIMPLE_DATETIME_TYPES = [0];
    var SIMPLE_CHANNEL_TYPES  = [1, 2, 3, 4, 5, 7, 13, 14, 15, 16];
    var SIMPLE_WIND_TYPES     = [6];
    var SIMPLE_FORECAST_TYPES = [8, 18];
    var SIMPLE_RADAR_TYPES    = [17];

    async function loadChannels() {
        if (cachedChannels) return cachedChannels;
        try {
            var r = await fetch('/api/admin/channels');
            cachedChannels = r.ok ? await r.json() : [];
        } catch (e) { cachedChannels = []; }
        return cachedChannels;
    }

    function fillChannelSelect(selId, channels, selected, addNone) {
        var sel = document.getElementById(selId);
        if (!sel) return;
        sel.innerHTML = addNone ? '<option value="">(none)</option>' : '<option value="">(select channel)</option>';
        (channels || []).forEach(function (ch) {
            var opt = document.createElement('option');
            opt.value = ch.name;
            opt.textContent = ch.value != null
                ? ch.name + ' [' + Number(ch.value).toFixed(1) + ' ' + ch.unit + ']'
                : ch.name;
            if (ch.name === selected) opt.selected = true;
            sel.appendChild(opt);
        });
    }

    function showSimpleSection(prefix, type, conf) {
        var sections = ['Datetime', 'Channel', 'Wind', 'Forecast', 'Radar', 'None'];
        sections.forEach(function (s) {
            var el = document.getElementById(prefix + 'Simple' + s);
            if (el) el.style.display = 'none';
        });

        if (SIMPLE_DATETIME_TYPES.indexOf(type) >= 0) {
            var el = document.getElementById(prefix + 'SimpleDatetime');
            if (el) el.style.display = '';
            var fmtEl = document.getElementById(prefix + 'TimeFormat');
            if (fmtEl) fmtEl.value = conf.format || '12h';
        } else if (SIMPLE_CHANNEL_TYPES.indexOf(type) >= 0) {
            var el = document.getElementById(prefix + 'SimpleChannel');
            if (el) el.style.display = '';
            fillChannelSelect(prefix + 'ChannelPick', cachedChannels, conf.channel, false);
        } else if (SIMPLE_WIND_TYPES.indexOf(type) >= 0) {
            var el = document.getElementById(prefix + 'SimpleWind');
            if (el) el.style.display = '';
            fillChannelSelect(prefix + 'WindSpeed', cachedChannels, conf.speedChannel    || 'wind.speed',      false);
            fillChannelSelect(prefix + 'WindDir',   cachedChannels, conf.directionChannel || 'wind.direction', false);
            fillChannelSelect(prefix + 'WindGust',  cachedChannels, conf.gustChannel     || '',                true);
        } else if (SIMPLE_FORECAST_TYPES.indexOf(type) >= 0) {
            var el = document.getElementById(prefix + 'SimpleForecast');
            if (el) el.style.display = '';
            var pEl = document.getElementById(prefix + 'Periods');
            if (pEl) pEl.value = conf.periods || (type === 18 ? 12 : 5);
        } else if (SIMPLE_RADAR_TYPES.indexOf(type) >= 0) {
            var el = document.getElementById(prefix + 'SimpleRadar');
            if (el) el.style.display = '';
            var urlEl = document.getElementById(prefix + 'RadarUrl');
            if (urlEl) urlEl.value = conf.tileUrl || '';
            var latEl = document.getElementById(prefix + 'RadarLat');
            if (latEl) latEl.value = conf.lat != null ? conf.lat : (cfg.stationLat || 39.5);
            var lonEl = document.getElementById(prefix + 'RadarLon');
            if (lonEl) lonEl.value = conf.lon != null ? conf.lon : (cfg.stationLon || -98.35);
            var zEl = document.getElementById(prefix + 'RadarZoom');
            if (zEl) zEl.value = conf.zoom != null ? conf.zoom : 5;
            var oEl = document.getElementById(prefix + 'RadarOpacity');
            if (oEl) oEl.value = conf.opacity != null ? conf.opacity : 0.6;
        } else {
            var el = document.getElementById(prefix + 'SimpleNone');
            if (el) el.style.display = '';
        }
    }

    function simpleToConfig(prefix, type) {
        var conf = {};
        if (SIMPLE_DATETIME_TYPES.indexOf(type) >= 0) {
            var fmtEl = document.getElementById(prefix + 'TimeFormat');
            if (fmtEl) conf.format = fmtEl.value || '12h';
        } else if (SIMPLE_CHANNEL_TYPES.indexOf(type) >= 0) {
            var ch = document.getElementById(prefix + 'ChannelPick');
            if (ch && ch.value) conf.channel = ch.value;
        } else if (SIMPLE_WIND_TYPES.indexOf(type) >= 0) {
            var sp = document.getElementById(prefix + 'WindSpeed');
            var di = document.getElementById(prefix + 'WindDir');
            var gu = document.getElementById(prefix + 'WindGust');
            if (sp && sp.value) conf.speedChannel     = sp.value;
            if (di && di.value) conf.directionChannel = di.value;
            if (gu && gu.value) conf.gustChannel      = gu.value;
        } else if (SIMPLE_FORECAST_TYPES.indexOf(type) >= 0) {
            var pEl = document.getElementById(prefix + 'Periods');
            if (pEl) { var p = parseInt(pEl.value, 10); if (p > 0) conf.periods = p; }
        } else if (SIMPLE_RADAR_TYPES.indexOf(type) >= 0) {
            var urlEl = document.getElementById(prefix + 'RadarUrl');
            var latEl = document.getElementById(prefix + 'RadarLat');
            var lonEl = document.getElementById(prefix + 'RadarLon');
            var zEl   = document.getElementById(prefix + 'RadarZoom');
            var oEl   = document.getElementById(prefix + 'RadarOpacity');
            if (urlEl && urlEl.value.trim()) conf.tileUrl = urlEl.value.trim();
            if (latEl && latEl.value !== '') conf.lat     = parseFloat(latEl.value);
            if (lonEl && lonEl.value !== '') conf.lon     = parseFloat(lonEl.value);
            if (zEl   && zEl.value   !== '') conf.zoom    = parseInt(zEl.value, 10);
            if (oEl   && oEl.value   !== '') conf.opacity = parseFloat(oEl.value);
        }
        return conf;
    }

    function applyMode(prefix, isSimple) {
        var simplePane = document.getElementById(prefix + 'SimplePane');
        var jsonPane   = document.getElementById(prefix + 'JsonPane');
        var simpleBtn  = document.getElementById(prefix + 'SimpleBtn');
        var jsonBtn    = document.getElementById(prefix + 'JsonBtn');
        if (simplePane) simplePane.style.display = isSimple ? '' : 'none';
        if (jsonPane)   jsonPane.style.display   = isSimple ? 'none' : '';
        if (simpleBtn)  simpleBtn.className = isSimple ? 'btn btn-sm btn-primary' : 'btn btn-sm btn-outline-secondary';
        if (jsonBtn)    jsonBtn.className   = isSimple ? 'btn btn-sm btn-outline-secondary' : 'btn btn-sm btn-primary';
    }

    window.setEwMode = function (mode) {
        ewModeIsSimple = mode === 'simple';
        applyMode('ew', ewModeIsSimple);
    };

    window.setAwMode = function (mode) {
        awModeIsSimple = mode === 'simple';
        applyMode('aw', awModeIsSimple);
    };

    window.onAwTypeChange = function () {
        var type = parseInt(document.getElementById('awType').value, 10);
        showSimpleSection('aw', type, {});
    };

    // ── Per-widget color controls ─────────────────────────────────────────────

    window.onColorToggle = function (prefix) {
        var en  = document.getElementById(prefix + 'ColorEnabled');
        var row = document.getElementById(prefix + 'ColorRow');
        if (row) row.style.display = (en && en.checked) ? '' : 'none';
    };

    function setColorControl(prefix, color) {
        var en  = document.getElementById(prefix + 'ColorEnabled');
        var col = document.getElementById(prefix + 'Color');
        if (en)  en.checked = !!color;
        if (col && color) col.value = color;
        onColorToggle(prefix);
    }

    function readColorControl(prefix) {
        var en  = document.getElementById(prefix + 'ColorEnabled');
        var col = document.getElementById(prefix + 'Color');
        return (en && en.checked && col) ? col.value : null;
    }

    // Merge (or strip) the color field into a config object based on the controls.
    function applyColorToConfig(prefix, conf) {
        var color = readColorControl(prefix);
        conf = conf || {};
        if (color) conf.color = color; else delete conf.color;
        return conf;
    }

    window.openEditWidgetModal = async function (id) {
        var def = allDefs.find(function (d) { return d.id === id; });
        if (!def) return;
        var conf = safeJson(def.config);
        document.getElementById('ewId').value    = id;
        document.getElementById('ewType').value  = def.type;
        document.getElementById('ewTitle').value = def.title || '';
        try {
            document.getElementById('ewConfig').value = def.config
                ? JSON.stringify(JSON.parse(def.config), null, 2)
                : '';
        } catch (e) {
            document.getElementById('ewConfig').value = def.config || '';
        }

        await loadChannels();
        ewModeIsSimple = true;
        applyMode('ew', true);
        showSimpleSection('ew', def.type, conf);
        setColorControl('ew', conf.color);

        new bootstrap.Modal('#editWidgetModal').show();
    };

    window.saveEditWidget = async function () {
        var id   = parseInt(document.getElementById('ewId').value, 10);
        var type = parseInt(document.getElementById('ewType').value, 10);
        var parsed;

        if (ewModeIsSimple) {
            parsed = simpleToConfig('ew', type);
        } else {
            var config = document.getElementById('ewConfig').value.trim();
            if (config) {
                try { parsed = JSON.parse(config); } catch (e) { alert('Config is not valid JSON.'); return; }
            }
            // Merge periods from dedicated input for forecast widgets in JSON mode
            if (type === 8 || type === 18) {
                var pEl = document.getElementById('ewPeriods');
                var periods = pEl ? parseInt(pEl.value, 10) : NaN;
                if (!isNaN(periods) && periods > 0) parsed = Object.assign(parsed || {}, { periods: periods });
            }
        }

        parsed = applyColorToConfig('ew', parsed);

        var r = await fetch('/api/dashboard/widgets/' + id + '/config', {
            method:  'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify(parsed || {})
        });
        if (!r.ok) { alert('Could not save widget config.'); return; }
        var def = allDefs.find(function (d) { return d.id === id; });
        if (def) def.config = JSON.stringify(parsed || {});
        bootstrap.Modal.getInstance(document.getElementById('editWidgetModal')).hide();
        renderAll();
    };

    window.openAddWidgetModal = async function () {
        document.getElementById('awTitle').value  = '';
        document.getElementById('awConfig').value = '';
        document.getElementById('awType').value   = '1';
        await loadChannels();
        awModeIsSimple = true;
        applyMode('aw', true);
        showSimpleSection('aw', 1, {});
        setColorControl('aw', null);
        new bootstrap.Modal('#addWidgetModal').show();
    };

    window.addWidget = async function () {
        var type  = parseInt(document.getElementById('awType').value, 10);
        var title = document.getElementById('awTitle').value.trim();
        if (!title) { alert('Title is required.'); return; }

        var configObj;
        if (awModeIsSimple) {
            configObj = simpleToConfig('aw', type);
        } else {
            var raw = document.getElementById('awConfig').value.trim();
            if (raw) {
                try { configObj = JSON.parse(raw); } catch (e) { alert('Config is not valid JSON.'); return; }
            }
        }

        configObj = applyColorToConfig('aw', configObj);

        var body = {
            type:          type,
            title:         title,
            config:        configObj ? JSON.stringify(configObj) : null,
            gridX:         0,
            gridY:         0,
            gridW:         4,
            gridH:         3,
            sortOrder:     (allDefs.length + 1) * 10,
            isVisible:     true,
            dashboardName: currentDash
        };

        var r = await fetch('/api/admin/widgets', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify(body)
        });

        if (!r.ok) { alert('Could not add widget: ' + await r.text()); return; }

        var w = await r.json();
        bootstrap.Modal.getInstance(document.getElementById('addWidgetModal')).hide();

        allDefs.push(w);

        var itemEl = grid.addWidget({
            id: String(w.id),
            x: 0, y: 0,
            w: w.gridW || 4,
            h: w.gridH || 3
        });

        if (itemEl) {
            var contentEl = itemEl.querySelector('.grid-stack-item-content');
            if (contentEl) contentEl.innerHTML = buildShell(w);
            var inner = itemEl.querySelector('[data-widget-id]');
            if (inner) renderWidget(inner);
        }
    };

    // ── Start ─────────────────────────────────────────────────────────────────

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
