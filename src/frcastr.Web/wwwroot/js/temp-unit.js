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
