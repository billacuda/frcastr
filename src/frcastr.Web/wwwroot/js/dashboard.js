(function () {
    'use strict';

    var cfg          = window.frcastrConfig || {};
    var pollMs       = (cfg.pollIntervalSeconds || 30) * 1000;
    var isMobile     = window.innerWidth < 768;
    var LAYOUT_KEY   = 'frcastr-layout';
    var MAX_HISTORY  = 60; // readings per channel kept in memory for sparklines

    var currentDash  = (new URLSearchParams(window.location.search)).get('dash') || 'Default';

    var grid          = null;
    var allDefs       = [];
    var latestData    = {};
    var channelHistory = {};
    var saveTimer     = null;
    var editMode      = false;

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

        grid = GridStack.init({
            column:        isMobile ? 2 : 12,
            cellHeight:    80,
            margin:        6,
            disableResize: isMobile || !cfg.isAuthenticated,
            disableDrag:   isMobile || !cfg.isAuthenticated,
            animate:       true
        }, '#dashboard-grid');

        if (cfg.isAuthenticated && !isMobile) {
            var gridEl = document.getElementById('dashboard-grid');
            if (gridEl) gridEl.classList.add('gs-editable');
        }

        allDefs.forEach(function (w) {
            var itemEl = grid.addWidget({
                id:       String(w.id),
                x:        w.gridX || 0,
                y:        w.gridY || 0,
                w:        isMobile ? 2 : (w.gridW || 4),
                h:        w.gridH || 3,
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

        // Wire change handler after a tick so initial load doesn't trigger a save
        setTimeout(function () {
            grid.on('change', function () {
                clearTimeout(saveTimer);
                saveTimer = setTimeout(saveLayout, 600);
            });
        }, 300);

        await refreshData();
        setInterval(refreshData, pollMs);
        setInterval(tickClocks, 1000);

        window.addEventListener('tempUnitChanged', function () { renderAll(); });
    }

    // ── Widget shell HTML ─────────────────────────────────────────────────────

    function buildShell(w) {
        return '<div class="widget-inner h-100 d-flex flex-column" data-widget-id="' + w.id + '" data-widget-type="' + w.type + '">' +
            '<div class="widget-titlebar d-flex align-items-center px-2 py-1">' +
            '<span class="widget-title small fw-semibold text-body-secondary flex-grow-1 text-truncate">' + escHtml(w.title) + '</span>' +
            '</div>' +
            '<div class="widget-body flex-grow-1 d-flex flex-column px-2 pb-2 overflow-hidden" data-widget-body></div>' +
            '</div>';
    }

    // ── Data refresh ──────────────────────────────────────────────────────────

    async function refreshData() {
        try {
            var results = await Promise.all([
                fetch('/api/weather/current').then(function (r)  { return r.json(); }).catch(function () { return null; }),
                fetch('/api/weather/forecast').then(function (r) { return r.json(); }).catch(function () { return null; }),
                fetch('/api/weather/moon').then(function (r)     { return r.json(); }).catch(function () { return null; }),
                fetch('/api/weather/sun').then(function (r)      { return r.json(); }).catch(function () { return null; }),
                fetch('/api/weather/alerts').then(function (r)   { return r.json(); }).catch(function () { return [];   })
            ]);

            var current  = results[0];
            var forecast = results[1];
            var moon     = results[2];
            var sun      = results[3];
            var alerts   = results[4];

            if (current && current.readings) {
                bufferReadings(current.readings);
            }

            latestData = { current: current, forecast: forecast, moon: moon, sun: sun, alerts: alerts, history: channelHistory };

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
        var body  = el.querySelector('[data-widget-body]');
        if (!body || !window.WeatherWidgets) return;
        WeatherWidgets.render(wType, body, conf, latestData);
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

        if (editMode) {
            document.querySelectorAll('[data-widget-id]').forEach(addRemoveButton);
        } else {
            document.querySelectorAll('.widget-remove-btn').forEach(function (b) { b.remove(); });
        }
    };

    function addRemoveButton(el) {
        if (el.querySelector('.widget-remove-btn')) return;
        var btn = document.createElement('button');
        btn.className = 'widget-remove-btn';
        btn.textContent = '×';
        btn.title = 'Remove widget';
        var wId = parseInt(el.dataset.widgetId, 10);
        btn.onclick = function (e) {
            e.stopPropagation();
            removeWidget(wId);
        };
        el.appendChild(btn);
    }

    async function removeWidget(id) {
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
    }

    window.openAddWidgetModal = function () {
        document.getElementById('awTitle').value  = '';
        document.getElementById('awConfig').value = '';
        document.getElementById('awType').value   = '1';
        new bootstrap.Modal('#addWidgetModal').show();
    };

    window.addWidget = async function () {
        var type     = parseInt(document.getElementById('awType').value);
        var title    = document.getElementById('awTitle').value.trim();
        var configRaw = document.getElementById('awConfig').value.trim();

        if (!title) { alert('Title is required.'); return; }

        var body = {
            type:          type,
            title:         title,
            config:        configRaw || null,
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
            if (inner) {
                addRemoveButton(inner);
                renderWidget(inner);
            }
        }
    };

    // ── Start ─────────────────────────────────────────────────────────────────

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
