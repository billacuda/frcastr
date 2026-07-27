// Dashboard test mode — simulated readings for laying out and previewing widgets.
//
// Administrators only, and enforced by the server: Index.cshtml renders this script tag and the
// panel markup inside @if (User.IsInRole("Administrator")), so a non-admin is never sent the file
// and window.TestMode does not exist for them.
//
// Nothing here is recorded. The module issues no request other than the GET that fills the channel
// list, never touches an ingest endpoint, and only ever mutates the in-memory object dashboard.js
// is about to render. State lives in localStorage and is discarded when you turn it off.
window.TestMode = (function () {
    'use strict';

    var KEY = 'frcastr-test-mode';

    function load() {
        try {
            var raw = JSON.parse(localStorage.getItem(KEY) || '{}');
            return { active: !!raw.active, values: raw.values || {} };
        } catch (e) {
            return { active: false, values: {} };
        }
    }

    function save(state) {
        localStorage.setItem(KEY, JSON.stringify(state));
        renderBanner();
    }

    var state = load();

    function isActive() {
        return state.active && Object.keys(state.values).length > 0;
    }

    // Units for fabricated channels. A channel the server has never seen has no unit to borrow,
    // and an empty one renders as a bare number.
    function unitFor(channel) {
        if (channel.indexOf('humidity') === 0)    return '%';
        if (channel.indexOf('temperature') === 0) return '°C';
        if (channel.indexOf('dewpoint') === 0)    return '°C';
        if (channel.indexOf('feelslike') === 0)   return '°C';
        if (channel.indexOf('windchill') === 0)   return '°C';
        if (channel.indexOf('heatindex') === 0)   return '°C';
        if (channel.indexOf('pressure') === 0)    return 'hPa';
        if (channel.indexOf('wind.speed') === 0)  return 'km/h';
        if (channel.indexOf('wind.direction') === 0) return '°';
        if (channel.indexOf('rainfall') === 0)    return 'mm';
        if (channel.indexOf('battery') === 0)     return 'V';
        return '';
    }

    /// Overlays the simulated values onto one poll's data. Called before anything else consumes
    /// it, so renders, sparkline buffers and stale badges all see the same picture.
    function apply(current, dailyExtremes) {
        if (!isActive()) return current;

        // Stand in for the whole response when the API is unreachable — previewing a layout should
        // not depend on the server being up.
        current = current || { readings: {}, staleChannels: [] };
        current.readings = current.readings || {};
        var now = new Date().toISOString();

        Object.keys(state.values).forEach(function (channel) {
            var v = Number(state.values[channel]);
            if (isNaN(v)) return;

            var existing = current.readings[channel];
            // Creating is the point, not just overwriting: it lets you preview temperature.water
            // or lightning before any such sensor exists.
            current.readings[channel] = {
                channelName: (existing && existing.channelName) || channel.split('@')[0],
                value:       v,
                unit:        (existing && existing.unit) || unitFor(channel),
                timestamp:   now,
                isCalculated: false
            };

            // A fabricated channel has no history, so a tile would show a value with an empty
            // high/low row. Only fill in what the server did not supply.
            if (dailyExtremes && dailyExtremes[channel] == null) {
                dailyExtremes[channel] = { min: v - 2, max: v + 2 };
            }
        });

        // The simulated timestamp is now, so an overridden channel must not still be flagged stale
        // from the real reading it replaced.
        if (Array.isArray(current.staleChannels)) {
            current.staleChannels = current.staleChannels.filter(function (c) {
                return state.values[c] == null;
            });
        }

        return current;
    }

    // ── Scenarios ─────────────────────────────────────────────────────────────
    // The interesting cases are combinations, not single values.

    var SCENARIOS = {
        'Freezing': {
            'temperature.outdoor': -8, 'humidity.outdoor': 80, 'wind.speed': 25,
            'feelslike.outdoor': -16, 'rainfall': 1, 'cloud.coverage': 90
        },
        'Hot & humid': {
            'temperature.outdoor': 35, 'humidity.outdoor': 75, 'wind.speed': 6,
            'feelslike.outdoor': 45, 'rainfall': 0, 'cloud.coverage': 10
        },
        'Storm': {
            'temperature.outdoor': 18, 'humidity.outdoor': 95, 'wind.speed': 65,
            'rainfall': 12, 'cloud.coverage': 100, 'lightning': 1, 'pressure': 985
        },
        'Pool day': {
            'temperature.outdoor': 31, 'humidity.outdoor': 45, 'wind.speed': 5,
            'temperature.water': 29, 'cloud.coverage': 5, 'rainfall': 0
        },
        'Cold plunge': {
            'temperature.water': 12, 'temperature.outdoor': 8, 'humidity.outdoor': 70
        },
        'Hot tub': {
            'temperature.water': 38, 'temperature.outdoor': 5, 'humidity.outdoor': 60
        }
    };

    // ── UI ────────────────────────────────────────────────────────────────────

    function esc(s) {
        return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/"/g, '&quot;')
            .replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function renderBanner() {
        var existing = document.getElementById('testModeBanner');
        if (!isActive()) {
            if (existing) existing.remove();
            document.body.classList.remove('test-mode-on');
            return;
        }
        document.body.classList.add('test-mode-on');
        if (existing) return;

        var bar = document.createElement('div');
        bar.id = 'testModeBanner';
        bar.className = 'test-mode-banner';
        bar.innerHTML =
            '<span class="fw-semibold">TEST MODE</span>' +
            '<span class="small">showing simulated readings — nothing is being recorded</span>' +
            '<button type="button" class="btn btn-sm btn-light py-0">Exit</button>';
        bar.querySelector('button').onclick = function () {
            state.active = false;
            save(state);
        };
        document.body.appendChild(bar);
    }

    function addRow(channel, value) {
        var rows = document.getElementById('tmRows');
        if (!rows) return;
        var row = document.createElement('div');
        row.className = 'input-group input-group-sm tm-row';
        row.innerHTML =
            '<input type="text" class="form-control tm-channel" list="tmChannelList" ' +
                   'placeholder="temperature.outdoor" value="' + esc(channel) + '" />' +
            '<input type="number" step="any" class="form-control tm-value" style="max-width:6.5rem" ' +
                   'value="' + esc(value) + '" />' +
            '<button type="button" class="btn btn-outline-danger" title="Remove">&times;</button>';
        row.querySelector('button').onclick = function () { row.remove(); };
        rows.appendChild(row);
    }

    function fillRows() {
        var rows = document.getElementById('tmRows');
        if (!rows) return;
        rows.innerHTML = '';
        var keys = Object.keys(state.values);
        if (!keys.length) { addRow('', ''); return; }
        keys.forEach(function (k) { addRow(k, state.values[k]); });
    }

    function collect() {
        var values = {};
        document.querySelectorAll('#tmRows .tm-row').forEach(function (row) {
            var c = row.querySelector('.tm-channel').value.trim();
            var v = row.querySelector('.tm-value').value;
            if (c && v !== '') values[c] = Number(v);
        });
        return values;
    }

    async function loadChannelList() {
        var list = document.getElementById('tmChannelList');
        if (!list || list.dataset.loaded) return;
        try {
            var r = await fetch('/api/admin/channels');
            if (!r.ok) return;
            var channels = await r.json();
            list.innerHTML = channels.map(function (c) {
                return '<option value="' + esc(c.name) + '"></option>';
            }).join('');
            list.dataset.loaded = '1';
        } catch (e) { /* free-text entry still works */ }
    }

    function open() {
        var panel = document.getElementById('testModePanel');
        if (!panel) return;
        state = load();
        fillRows();
        var toggle = document.getElementById('tmActive');
        if (toggle) toggle.checked = state.active;
        loadChannelList();
        new bootstrap.Offcanvas(panel).show();
    }

    function applyPanel() {
        state.values = collect();
        var toggle = document.getElementById('tmActive');
        state.active = toggle ? toggle.checked : true;
        save(state);
        if (window.frcastrRefresh) window.frcastrRefresh();
    }

    function clearAll() {
        state = { active: false, values: {} };
        save(state);
        fillRows();
        var toggle = document.getElementById('tmActive');
        if (toggle) toggle.checked = false;
        if (window.frcastrRefresh) window.frcastrRefresh();
    }

    function applyScenario(name) {
        var preset = SCENARIOS[name];
        if (!preset) return;
        var rows = document.getElementById('tmRows');
        if (rows) rows.innerHTML = '';
        Object.keys(preset).forEach(function (k) { addRow(k, preset[k]); });
        var toggle = document.getElementById('tmActive');
        if (toggle) toggle.checked = true;
    }

    function scenarioNames() {
        return Object.keys(SCENARIOS);
    }

    document.addEventListener('DOMContentLoaded', renderBanner);

    return {
        isActive: isActive,
        apply: apply,
        open: open,
        applyPanel: applyPanel,
        clearAll: clearAll,
        addRow: addRow,
        applyScenario: applyScenario,
        scenarioNames: scenarioNames
    };
})();
