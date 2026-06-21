(function () {
    'use strict';

    function getCurrentDash() {
        var params = new URLSearchParams(window.location.search);
        return params.get('dash') || 'Default';
    }

    function buildMenu(names) {
        var menu = document.getElementById('dashboardMenu');
        if (!menu) return;

        var current = getCurrentDash();
        var others = names.filter(function (n) { return n !== 'Default'; }).sort();

        var html = '<li><a class="dropdown-item' + (current === 'Default' ? ' active' : '') +
            '" href="/?dash=Default">Default</a></li>';

        if (others.length) {
            html += '<li><hr class="dropdown-divider"></li>';
            others.forEach(function (n) {
                html += '<li><a class="dropdown-item' + (current === n ? ' active' : '') +
                    '" href="/?dash=' + encodeURIComponent(n) + '">' + escHtml(n) + '</a></li>';
            });
        }

        html += '<li><hr class="dropdown-divider"></li>' +
            '<li><a class="dropdown-item" href="#" id="newDashboardBtn">+ New Dashboard</a></li>';

        menu.innerHTML = html;

        document.getElementById('newDashboardBtn').addEventListener('click', function (e) {
            e.preventDefault();
            var name = prompt('Dashboard name:');
            if (!name || !name.trim()) return;
            name = name.trim();
            fetch('/api/dashboard?name=' + encodeURIComponent(name), { method: 'POST' })
                .then(function () {
                    window.location.href = '/?dash=' + encodeURIComponent(name);
                })
                .catch(function () {
                    alert('Could not create dashboard.');
                });
        });
    }

    function updateToggleLabel() {
        var toggle = document.getElementById('dashboardDropdownToggle');
        if (!toggle) return;
        var current = getCurrentDash();
        toggle.textContent = current === 'Default' ? 'Dashboard' : 'Dashboard · ' + current;
        // Re-append the caret Bootstrap expects for dropdown-toggle
        var caret = document.createElement('span');
        caret.className = 'visually-hidden';
        caret.textContent = 'Toggle dropdown';
        toggle.appendChild(caret);
    }

    function init() {
        updateToggleLabel();

        fetch('/api/dashboard/names')
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (names) { buildMenu(Array.isArray(names) ? names : []); })
            .catch(function () {});
    }

    function escHtml(s) {
        return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
