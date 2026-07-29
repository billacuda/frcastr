(function () {
    'use strict';

    var UNIT_KEY = 'frcastr-temp-unit';

    window.getTempUnit = function () {
        return localStorage.getItem(UNIT_KEY) || 'C';
    };

    window.toggleTempUnit = function () {
        var next = getTempUnit() === 'C' ? 'F' : 'C';
        try { localStorage.setItem(UNIT_KEY, next); } catch (e) {}
        updateTempUnitUI(next);
        window.dispatchEvent(new CustomEvent('tempUnitChanged', { detail: next }));
    };

    // Converts a raw Celsius reading to the display unit and formats it, honoring
    // Display.TemperatureDecimals. Returns an HTML string — callers append °<unit> themselves.
    window.formatTemp = function (celsiusValue) {
        if (celsiusValue == null || isNaN(celsiusValue)) return '–';
        var v = window.getTempUnit() === 'F' ? (celsiusValue * 9 / 5 + 32) : celsiusValue;
        var decimals = (window.frcastrConfig && window.frcastrConfig.temperatureDecimals) ? 1 : 0;
        if (!decimals) return String(Math.round(v));
        var fixed = v.toFixed(1);
        var dot = fixed.indexOf('.');
        return fixed.slice(0, dot) + '<span class="temp-tenths">' + fixed.slice(dot) + '</span>';
    };

    function updateTempUnitUI(unit) {
        var cEl = document.getElementById('tempUnitC');
        var fEl = document.getElementById('tempUnitF');
        if (!cEl || !fEl) return;
        if (unit === 'C') {
            cEl.className = 'fw-bold'; cEl.style.fontSize = '';
            fEl.className = 'text-muted'; fEl.style.fontSize = '0.85em';
        } else {
            fEl.className = 'fw-bold'; fEl.style.fontSize = '';
            cEl.className = 'text-muted'; cEl.style.fontSize = '0.85em';
        }
    }

    updateTempUnitUI(getTempUnit());
})();
