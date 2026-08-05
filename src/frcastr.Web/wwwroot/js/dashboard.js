(function () {
    'use strict';

    var cfg          = window.frcastrConfig || {};
    var pollMs       = (cfg.pollIntervalSeconds || 30) * 1000;
    // Phones get a layout of their own. This is a live media query rather than a one-shot
    // innerWidth read so that rotating a phone — or unfolding a foldable — lands in the right
    // mode instead of keeping whichever one the first paint happened to see. 767.98px is
    // Bootstrap's md boundary, the same one the @media rule in site.css uses.
    var phoneQuery   = window.matchMedia('(max-width: 767.98px)');
    var isMobile     = phoneQuery.matches;
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
    var savedLayouts  = {}; // the /api/dashboard/layout response, kept for rebuilds

    // Per dashboard, set from the Dashboard menu: keep widgets that sit side by side on the
    // desktop side by side on a phone, two to a row, instead of one widget per row. Rendered into
    // frcastrConfig by IndexModel so both this and the menu start from the same value.
    var mobileTwoCol = !!cfg.mobileTwoColumn;

    // ── Mobile sizing ─────────────────────────────────────────────────────────
    //
    // A phone widget's width is settled by the stack, so its height alone decides whether it is
    // readable and a desktop row count means nothing here — carrying one over is what made
    // every widget a letterbox. Each type gets a width:height ratio instead, and one grid row
    // is a twelfth of the grid's width (see updateCellHeight), so a full-width widget h rows tall
    // lands at 12/h whatever the phone — and a half-width one at 6/h, since it has half the width
    // to be in ratio with. A type that isn't listed falls back to the default rather than a lookup
    // miss, so a widget type added later still comes out sensible.
    var MOBILE_ROWS_PER_WIDTH = 12;
    var MOBILE_ASPECT_DEFAULT = 2.0;
    var MOBILE_ASPECT = {
        // Single-value tiles: a wide card, like a phone weather app's rows.
        0: 2.4, 3: 2.4, 4: 2.4, 5: 2.4,
        10: 2.4, 13: 2.4, 14: 2.4, 15: 2.4,
        // Not single-value after all: the temperature and AQI tiles carry a second reading, a
        // high/low line and a timestamp under the value. Four stacked lines in a 2.4:1 card leave
        // each one too little to read, so they get a card half again as tall.
        1: 1.9, 2: 1.9, 16: 1.9,
        // Two-line tiles.
        9: 2.0, 11: 2.0, 12: 2.0,
        // Forecast strips: several columns of four stacked lines each.
        8: 1.8, 18: 1.8,
        // Dials and animated scenes need the height to read as a picture.
        6: 1.6, 7: 1.6, 19: 1.6,
        // A map is worth nothing letterboxed.
        17: 1.1
    };

    // A floor under the aspect ratio, in rows. Ratio alone sizes a widget off the width it was
    // given, which is fine until the width is half a phone: a tile of four stacked lines paired
    // beside another comes out too short for any of them, however good the ratio looks. Types that
    // stack more than a value and a timestamp claim a minimum here, so the pair is as tall as its
    // contents need rather than as tall as half a screen implies.
    var MOBILE_MIN_ROWS_DEFAULT = 3;
    var MOBILE_MIN_ROWS = {
        1: 5, 2: 5, 16: 5, // value + second reading + high/low + timestamp
        3: 4, 4: 4         // value + dew point + timestamp
    };

    // fraction: how much of the grid's width this widget gets — 1 full width, 0.5 one of a pair.
    function mobileHeight(type, fraction) {
        var aspect = MOBILE_ASPECT[type] || MOBILE_ASPECT_DEFAULT;
        var floor  = MOBILE_MIN_ROWS[type] || MOBILE_MIN_ROWS_DEFAULT;
        return Math.max(floor, Math.round(MOBILE_ROWS_PER_WIDTH * fraction / aspect));
    }

    // Which widgets shared a row on the desktop. A row is defined by the vertical span of its
    // first widget — anything that starts before that widget ends was beside it — rather than by
    // the span of the row so far, which a tall widget would keep extending until unrelated rows
    // chained into one.
    function desktopRows(ordered, posOf) {
        var rows = [], bottom = -1;
        ordered.forEach(function (d) {
            var p = posOf(d);
            if (rows.length && p.y < bottom) {
                rows[rows.length - 1].push(d);
            } else {
                rows.push([d]);
                bottom = p.y + p.h;
            }
        });
        return rows;
    }

    // Ordered the way the desktop layout reads: top to bottom, then left to right. Nothing has to
    // be authored — every dashboard that works on a desktop gets a usable phone view for free,
    // which is why the saved mobile layout is neither read nor written (see saveLayout).
    //
    // One column gives every widget a row of its own. Two columns keep desktop neighbors together
    // in pairs; a widget alone in its desktop row still spans the phone's width, and so does the
    // odd one out of a row of three.
    function mobileNodes(defs, desktopLayout) {
        var pos = {};
        (desktopLayout || []).forEach(function (n) { pos[String(n.id)] = n; });

        var posOf = function (d) {
            var p = pos[String(d.id)];
            return {
                x: (p ? p.x : d.gridX) || 0,
                y: (p ? p.y : d.gridY) || 0,
                h: (p ? p.h : d.gridH) || 1
            };
        };

        var ordered = defs.slice().sort(function (a, b) {
            var pa = posOf(a), pb = posOf(b);
            return pa.y - pb.y || pa.x - pb.x;
        });

        var cols  = mobileTwoCol ? 2 : 1;
        var nodes = [];
        var y     = 0;

        desktopRows(ordered, posOf).forEach(function (row) {
            for (var i = 0; i < row.length; i += cols) {
                var pair = row.slice(i, i + cols);
                if (pair.length < 2) {
                    var h = mobileHeight(pair[0].type, 1);
                    nodes.push({ def: pair[0], x: 0, y: y, w: cols, h: h });
                    y += h;
                } else {
                    // Both take the taller of the two so the row reads as one band.
                    var hp = Math.max(mobileHeight(pair[0].type, 0.5), mobileHeight(pair[1].type, 0.5));
                    nodes.push({ def: pair[0], x: 0, y: y, w: 1, h: hp });
                    nodes.push({ def: pair[1], x: 1, y: y, w: 1, h: hp });
                    y += hp;
                }
            }
        });

        return nodes;
    }

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

        // Phones: a row is a twelfth of the width, so each widget keeps the aspect ratio its
        // type asked for and the page scrolls to whatever length that adds up to. Desktops
        // still fit their whole layout into the viewport.
        if (isMobile) {
            var gridEl = document.getElementById('dashboard-grid');
            var width  = (gridEl && gridEl.clientWidth) || window.innerWidth;
            grid.cellHeight(Math.max(Math.floor(width / MOBILE_ROWS_PER_WIDTH), 8));
            return;
        }

        var nodes = grid.save(false) || [];
        var maxRow = 0;
        nodes.forEach(function (n) { var b = (n.y || 0) + (n.h || 1); if (b > maxRow) maxRow = b; });
        if (!maxRow) maxRow = maxRowFromDefs(allDefs);
        grid.cellHeight(computeCellHeight(maxRow));
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
        savedLayouts = layoutResp;

        buildGrid();

        // Crossing the breakpoint — rotating a phone, resizing a window — rebuilds the grid in
        // the other mode. Everything it needs is already in hand, so nothing is refetched.
        var onModeChange = function (e) {
            if (e.matches === isMobile) return;
            isMobile = e.matches;
            buildGrid();
        };
        if (phoneQuery.addEventListener) phoneQuery.addEventListener('change', onModeChange);
        else if (phoneQuery.addListener) phoneQuery.addListener(onModeChange); // Safari < 14

        // The Dashboard menu's "Two columns on phones" switch. Saved there; applied here, without
        // a reload, so a narrow window shows the result straight away.
        window.addEventListener('mobileColumnsChanged', function (e) {
            var want = !!(e.detail && e.detail.twoColumn);
            if (want === mobileTwoCol) return;
            mobileTwoCol = want;
            if (isMobile) buildGrid();
        });

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

        // Lets test mode redraw immediately on apply rather than waiting for the next poll.
        window.frcastrRefresh = refreshData;
        setInterval(tickClocks, 1000);

        window.addEventListener('tempUnitChanged', function () { renderAll(); });

        // The density sliders preview live on the widget being edited, so closing the modal without
        // saving has to put the saved config back.
        var editModalEl = document.getElementById('editWidgetModal');
        if (editModalEl) editModalEl.addEventListener('hidden.bs.modal', function () { renderAll(); });
    }

    // ── Grid build ────────────────────────────────────────────────────────────
    //
    // Separate from init() because the two modes are different grids, not one grid with a flag:
    // crossing the phone breakpoint tears this down and builds the other one from the widget
    // definitions and layouts already fetched.

    function buildGrid() {
        var container = document.getElementById('dashboard-grid');
        if (!container) return;

        if (grid) {
            // Let widgets release anything that outlives their markup — the radar's Leaflet map
            // would otherwise keep fetching tiles for an element that no longer exists.
            container.querySelectorAll('[data-widget-body]').forEach(function (body) {
                if (window.WeatherWidgets && WeatherWidgets.destroy) WeatherWidgets.destroy(body);
            });
            grid.destroy(false); // keep the container itself; its items go below
            grid = null;
            window.frcastrGrid = null;
            container.innerHTML = '';
        }

        // A widget can opt out of the phone view; see applyMobileToConfig.
        var defs = isMobile
            ? allDefs.filter(function (d) { return !safeJson(d.config).hideOnMobile; })
            : allDefs;

        if (!defs.length) {
            container.innerHTML =
                '<div class="d-flex align-items-center justify-content-center py-5 text-body-secondary text-center px-3">' +
                (allDefs.length
                    ? 'Every widget on this dashboard is hidden on phones.'
                    : 'No widgets configured. Log in and visit Admin &rsaquo; Widgets to add widgets.') +
                '</div>';
            return;
        }

        grid = GridStack.init({
            column:        isMobile ? (mobileTwoCol ? 2 : 1) : 12,
            cellHeight:    20, // placeholder; updateCellHeight() sets the real value below
            margin:        6,
            minH:          1,
            handle:        '.widget-titlebar',
            disableResize: isMobile || !cfg.isAuthenticated,
            disableDrag:   isMobile || !cfg.isAuthenticated,
            animate:       true
        }, '#dashboard-grid');

        container.classList.toggle('gs-editable', cfg.isAuthenticated && !isMobile);

        // Expose grid for widgets that need to temporarily disable drag (e.g. Radar)
        window.frcastrGrid = grid;

        if (isMobile) {
            var desktopLayout = parseLayout(savedLayouts.desktop) || getLocalLayout('desktop');
            mobileNodes(defs, desktopLayout).forEach(function (n) {
                addShell(grid.addWidget({
                    id: String(n.def.id), x: n.x, y: n.y, w: n.w, h: n.h, noResize: true, noMove: true
                }), n.def);
            });
        } else {
            defs.forEach(function (w) {
                addShell(grid.addWidget({
                    id:   String(w.id),
                    x:    w.gridX || 0,
                    y:    w.gridY || 0,
                    w:    w.gridW || 4,
                    h:    w.gridH || 2,
                    minH: 1
                }), w);
            });

            // Apply saved layout (server preferred, localStorage fallback)
            var saved = parseLayout(savedLayouts.desktop) || getLocalLayout('desktop');
            if (saved && saved.length) grid.load(saved, false);
        }

        // Set cell height after the layout is applied
        updateCellHeight();

        // Wire change handler after a tick so initial load doesn't trigger a save
        setTimeout(function () {
            if (!grid) return;
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

        renderAll();
    }

    function addShell(itemEl, def) {
        if (!itemEl) return;
        var contentEl = itemEl.querySelector('.grid-stack-item-content');
        if (contentEl) contentEl.innerHTML = buildShell(def);
    }

    // ── Widget shell HTML ─────────────────────────────────────────────────────

    function buildShell(w) {
        var btnHtml = cfg.isAdmin
            ? '<button class="widget-action-btn" title="Edit widget" onclick="openEditWidgetModal(' + w.id + ')">&#x270F;</button>' +
              '<button class="widget-action-btn" title="Remove widget" onclick="confirmRemoveWidget(' + w.id + ')">&#x2715;</button>'
            : '';
        // Titlebar and body padding live in site.css so they can be driven per widget by
        // --widget-pad; see applyWidgetDensity().
        return '<div class="widget-inner h-100 d-flex flex-column" data-widget-id="' + w.id + '" data-widget-type="' + w.type + '">' +
            '<div class="widget-titlebar d-flex align-items-center">' +
            '<span class="widget-title small fw-semibold text-body-secondary flex-grow-1 text-truncate">' + escHtml(w.title) + '</span>' +
            btnHtml +
            '</div>' +
            '<div class="widget-body flex-grow-1 d-flex flex-column overflow-hidden" data-widget-body></div>' +
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

            // Test mode (admin-only, never loaded for anyone else) overlays simulated readings
            // here, before anything consumes them, so renders, sparkline buffers and stale badges
            // all see one consistent picture. Nothing is sent anywhere — see test-mode.js.
            if (window.TestMode && TestMode.isActive()) {
                dailyExtremes = dailyExtremes || {};   // so fabricated channels can get a high/low
                current = TestMode.apply(current, dailyExtremes);
            }

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
        applyWidgetDensity(el, conf);
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

    // Per-widget density: padding and text scale ride CSS variables the same way --widget-color
    // does, so all 20 widget types inherit them without any renderer knowing they exist. Clearing
    // the property rather than writing a default lets the site.css baseline win, so widgets with no
    // override keep tracking it.
    function applyWidgetDensity(el, conf) {
        if (!el) return;
        conf = conf || {};
        var pad = Number(conf.pad);
        if (conf.pad != null && isFinite(pad)) el.style.setProperty('--widget-pad', pad + 'px');
        else el.style.removeProperty('--widget-pad');

        var scale = Number(conf.fontScale);
        if (isFinite(scale) && scale > 0) el.style.setProperty('--widget-font-scale', scale);
        else el.style.removeProperty('--widget-font-scale');
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
            case 13: return [config.channel || 'feelslike.outdoor'];
            case 14: return [config.channel || 'rainfall'];
            case 15: return [config.channel || 'pressure'];
            case 16: return [config.channel || 'aqi.outdoor'];
            case 19: return [config.channel || 'temperature.water'];
            default: return [];
        }
    }

    // ── Layout save ───────────────────────────────────────────────────────────

    async function saveLayout() {
        if (!grid) return;
        // The phone stack is derived from the desktop layout, so there is nothing here worth
        // keeping — and gridstack fires 'change' during its own compaction, which used to let a
        // machine-generated arrangement overwrite the saved one. The server's LayoutJsonMobile
        // column stays where it is, now unwritten.
        if (isMobile) return;

        var items = grid.save(false);
        var layout = items.map(function (n) {
            return { id: n.id, x: n.x, y: n.y, w: n.w, h: n.h };
        });
        var layoutJson = JSON.stringify(layout);

        if (!cfg.isAuthenticated) {
            setLocalLayout('desktop', layout);
            return;
        }

        try {
            var resp = await csrfFetch('/api/dashboard/layout?dashboard=' + encodeURIComponent(currentDash), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ desktop: layoutJson, mobile: null })
            });
            if (resp.status === 401) {
                setLocalLayout('desktop', layout);
            }
        } catch (e) {
            setLocalLayout('desktop', layout);
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
            await csrfFetch('/api/dashboard/widgets/' + id + '/config', {
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

        var r = await csrfFetch('/api/admin/widgets/' + id, { method: 'DELETE' });
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
    var SIMPLE_WATER_TYPES    = [19];

    // Band thresholds are stored in °C to match the readings, but shown in whatever unit the site
    // is currently displaying — the defaults below are 77 / 84 / 90 °F.
    var WATER_BAND_DEFAULTS = { icy: 25, cool: 28.9, warm: 32.2 };

    function bandToDisplay(c) {
        var u = window.getTempUnit ? window.getTempUnit() : 'C';
        return u === 'F' ? Math.round(c * 9 / 5 + 32) : Math.round(c * 10) / 10;
    }

    function bandToCelsius(v) {
        var u = window.getTempUnit ? window.getTempUnit() : 'C';
        return u === 'F' ? Math.round(((v - 32) * 5 / 9) * 10) / 10 : Number(v);
    }

    var cachedLabels = null;

    async function loadChannels() {
        if (cachedChannels) return cachedChannels;
        try {
            var r = await fetch('/api/admin/channels');
            cachedChannels = r.ok ? await r.json() : [];
        } catch (e) { cachedChannels = []; }
        try {
            var lr = await fetch('/api/weather/channel-labels');
            cachedLabels = lr.ok ? await lr.json() : {};
        } catch (e) { cachedLabels = {}; }
        return cachedChannels;
    }

    // Display label for a channel key: exact key, then canonical channel, then the key itself.
    function channelLabel(key) {
        if (!cachedLabels) return key;
        var bare = key.indexOf('@') > 0 ? key.slice(0, key.indexOf('@')) : key;
        return cachedLabels[key] || cachedLabels[bare] || key;
    }

    function fillChannelSelect(selId, channels, selected, addNone) {
        var sel = document.getElementById(selId);
        if (!sel) return;
        sel.innerHTML = addNone ? '<option value="">(none)</option>' : '<option value="">(select channel)</option>';
        (channels || []).forEach(function (ch) {
            var opt = document.createElement('option');
            // The value stays the channel key — widgets bind by key, so a label can be renamed or
            // removed without touching any widget's config.
            opt.value = ch.name;
            var label = channelLabel(ch.name);
            opt.textContent = ch.value != null
                ? label + ' [' + Number(ch.value).toFixed(1) + ' ' + ch.unit + ']'
                : label;
            if (label !== ch.name) opt.title = ch.name;
            if (ch.name === selected) opt.selected = true;
            sel.appendChild(opt);
        });
    }

    function showSimpleSection(prefix, type, conf) {
        var sections = ['Datetime', 'Channel', 'Wind', 'Forecast', 'Radar', 'Water', 'None'];
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
            if (pEl) pEl.value = conf.periods || (type === 18 ? 12 : 7);
        } else if (SIMPLE_WATER_TYPES.indexOf(type) >= 0) {
            var el = document.getElementById(prefix + 'SimpleWater');
            if (el) el.style.display = '';
            fillChannelSelect(prefix + 'WaterChannel', cachedChannels, conf.channel, false);
            var bands = conf.bands || {};
            var unitEl = document.getElementById(prefix + 'WaterBandUnit');
            if (unitEl) unitEl.textContent = window.getTempUnit ? window.getTempUnit() : 'C';
            ['icy', 'cool', 'warm'].forEach(function (k) {
                var input = document.getElementById(prefix + 'Band' + k);
                if (input) {
                    input.value = bandToDisplay(bands[k] != null ? Number(bands[k]) : WATER_BAND_DEFAULTS[k]);
                }
            });
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
        } else if (SIMPLE_WATER_TYPES.indexOf(type) >= 0) {
            var wch = document.getElementById(prefix + 'WaterChannel');
            if (wch && wch.value) conf.channel = wch.value;
            var bands = {};
            ['icy', 'cool', 'warm'].forEach(function (k) {
                var input = document.getElementById(prefix + 'Band' + k);
                if (input && input.value !== '') bands[k] = bandToCelsius(parseFloat(input.value));
            });
            if (Object.keys(bands).length) conf.bands = bands;
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

    // ── Hide on phones ────────────────────────────────────────────────────────
    // Same merge-or-strip convention as color and density: a widget that isn't hidden stores no
    // key at all. Applied after simpleToConfig(), so it works in both Simple and JSON modes.

    function setMobileControl(prefix, conf) {
        var el = document.getElementById(prefix + 'HideMobile');
        if (el) el.checked = !!(conf && conf.hideOnMobile);
    }

    function applyMobileToConfig(prefix, conf) {
        var el = document.getElementById(prefix + 'HideMobile');
        conf = conf || {};
        if (el && el.checked) conf.hideOnMobile = true; else delete conf.hideOnMobile;
        return conf;
    }

    // ── Per-widget density controls ───────────────────────────────────────────
    // Must match the baseline in site.css: a widget sitting on the defaults stores neither key.

    var DEFAULT_PAD = 4, DEFAULT_FONT_PCT = 100;

    function setDensityControls(prefix, conf) {
        conf = conf || {};
        var padEl  = document.getElementById(prefix + 'Pad');
        var fontEl = document.getElementById(prefix + 'FontScale');
        if (padEl)  padEl.value  = (conf.pad != null && isFinite(Number(conf.pad))) ? Number(conf.pad) : DEFAULT_PAD;
        if (fontEl) fontEl.value = (conf.fontScale > 0) ? Math.round(Number(conf.fontScale) * 100) : DEFAULT_FONT_PCT;
        updateDensityLabels(prefix);
    }

    function readDensityControls(prefix) {
        var padEl  = document.getElementById(prefix + 'Pad');
        var fontEl = document.getElementById(prefix + 'FontScale');
        return {
            pad:       padEl  ? parseInt(padEl.value, 10)  : DEFAULT_PAD,
            fontPct:   fontEl ? parseInt(fontEl.value, 10) : DEFAULT_FONT_PCT
        };
    }

    function updateDensityLabels(prefix) {
        var d       = readDensityControls(prefix);
        var padVal  = document.getElementById(prefix + 'PadVal');
        var fontVal = document.getElementById(prefix + 'FontScaleVal');
        if (padVal)  padVal.textContent  = d.pad + 'px';
        if (fontVal) fontVal.textContent = d.fontPct + '%';
    }

    // Merge (or strip) the density fields. Values equal to the defaults are deleted so an untouched
    // widget keeps a clean config and keeps tracking any future change to the baseline.
    function applyDensityToConfig(prefix, conf) {
        var d = readDensityControls(prefix);
        conf = conf || {};
        if (isFinite(d.pad) && d.pad !== DEFAULT_PAD) conf.pad = d.pad; else delete conf.pad;
        if (isFinite(d.fontPct) && d.fontPct !== DEFAULT_FONT_PCT) conf.fontScale = d.fontPct / 100;
        else delete conf.fontScale;
        return conf;
    }

    window.onDensityInput = function (prefix) {
        updateDensityLabels(prefix);
        // Preview live on the widget being edited. The hidden.bs.modal handler below re-renders
        // from the saved config, so cancelling reverts.
        if (prefix !== 'ew') return;
        var idEl = document.getElementById('ewId');
        var el   = idEl && document.querySelector('[data-widget-id="' + idEl.value + '"]');
        if (el) applyWidgetDensity(el, applyDensityToConfig('ew', {}));
    };

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
        setDensityControls('ew', conf);
        setMobileControl('ew', conf);

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
        parsed = applyDensityToConfig('ew', parsed);
        parsed = applyMobileToConfig('ew', parsed);

        var r = await csrfFetch('/api/dashboard/widgets/' + id + '/config', {
            method:  'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify(parsed || {})
        });
        if (!r.ok) { alert('Could not save widget config.'); return; }
        var def = allDefs.find(function (d) { return d.id === id; });
        if (def) def.config = JSON.stringify(parsed || {});
        bootstrap.Modal.getInstance(document.getElementById('editWidgetModal')).hide();
        // "Hide on phones" changes which widgets the stack contains, so the phone view rebuilds
        // rather than re-rendering what is already there.
        if (isMobile) buildGrid(); else renderAll();
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
        setDensityControls('aw', {});
        setMobileControl('aw', {});
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
        configObj = applyDensityToConfig('aw', configObj);
        configObj = applyMobileToConfig('aw', configObj);

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

        var r = await csrfFetch('/api/admin/widgets', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify(body)
        });

        if (!r.ok) { alert('Could not add widget: ' + await r.text()); return; }

        var w = await r.json();
        bootstrap.Modal.getInstance(document.getElementById('addWidgetModal')).hide();

        allDefs.push(w);

        // The phone stack is ordered and sized as a whole, so it rebuilds rather than having a
        // widget spliced into it at desktop dimensions.
        if (isMobile) { buildGrid(); return; }

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
