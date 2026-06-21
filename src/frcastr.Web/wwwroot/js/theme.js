(function () {
    'use strict';

    var STORAGE_KEY = 'frcastr-theme';
    var html = document.getElementById('htmlRoot');

    function updateThemeIcon() {
        var icon = document.getElementById('themeIcon');
        if (!icon) return;
        var theme = html ? html.getAttribute('data-bs-theme') : 'light';
        icon.textContent = theme === 'dark' ? '☀️' : '🌙';
    }

    function applyTheme(theme) {
        if (!html) return;
        if (theme === 'auto') {
            var prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
            html.setAttribute('data-bs-theme', prefersDark ? 'dark' : 'light');
        } else {
            html.setAttribute('data-bs-theme', theme);
        }
        updateThemeIcon();
    }

    function getActiveTheme() {
        return localStorage.getItem(STORAGE_KEY) || (window.frcastrConfig && window.frcastrConfig.theme) || 'auto';
    }

    window.toggleTheme = function () {
        var current = html ? html.getAttribute('data-bs-theme') : 'light';
        var next = current === 'dark' ? 'light' : 'dark';
        try { localStorage.setItem(STORAGE_KEY, next); } catch (e) {}
        applyTheme(next);
    };

    applyTheme(getActiveTheme());

    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
        if (getActiveTheme() === 'auto') applyTheme('auto');
    });
})();
