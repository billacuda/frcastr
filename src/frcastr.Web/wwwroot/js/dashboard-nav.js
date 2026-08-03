(function () {
    'use strict';

    function getCurrentDash() {
        var params = new URLSearchParams(window.location.search);
        return params.get('dash') || 'Default';
    }

    function dashCopyBtn(name) {
        return '<button class="btn btn-link btn-sm text-body-secondary p-0 pe-1" title="Copy dashboard" ' +
            "onclick='copyDashboard(event," + JSON.stringify(name) + ")'>&#x2398;</button>";
    }

    function buildMenu(names) {
        var menu = document.getElementById('dashboardMenu');
        if (!menu) return;

        var current = getCurrentDash();
        var others = names.filter(function (n) { return n !== 'Default'; }).sort();

        var html = '<li class="d-flex align-items-center">' +
            '<a class="dropdown-item flex-grow-1' + (current === 'Default' ? ' active' : '') +
            '" href="/?dash=Default">Default</a>' +
            dashCopyBtn('Default') + '</li>';

        if (others.length) {
            html += '<li><hr class="dropdown-divider"></li>';
            others.forEach(function (n) {
                html += '<li class="d-flex align-items-center">' +
                    '<a class="dropdown-item flex-grow-1' + (current === n ? ' active' : '') +
                    '" href="/?dash=' + encodeURIComponent(n) + '">' + escHtml(n) + '</a>' +
                    dashCopyBtn(n) +
                    '<button class="btn btn-link btn-sm text-danger p-0 pe-2" title="Delete dashboard" ' +
                    "onclick='deleteDashboard(event," + JSON.stringify(n) + ")'>&times;</button></li>";
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
            csrfFetch('/api/dashboard?name=' + encodeURIComponent(name), { method: 'POST' })
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

    window.copyDashboard = function (e, name) {
        e.preventDefault();
        e.stopPropagation();
        var newName = prompt('Copy "' + name + '" to new dashboard name:');
        if (!newName || !newName.trim()) return;
        newName = newName.trim();
        csrfFetch('/api/dashboard/copy?from=' + encodeURIComponent(name) + '&to=' + encodeURIComponent(newName), { method: 'POST' })
            .then(function (r) {
                if (r.ok) {
                    window.location.href = '/?dash=' + encodeURIComponent(newName);
                } else {
                    r.text().then(function (t) { alert('Could not copy dashboard: ' + t); });
                }
            })
            .catch(function () { alert('Could not copy dashboard.'); });
    };

    window.deleteDashboard = function (e, name) {
        e.preventDefault();
        e.stopPropagation();
        if (!confirm('Delete dashboard "' + name + '" and all its widgets?')) return;
        csrfFetch('/api/dashboard?name=' + encodeURIComponent(name), { method: 'DELETE' })
            .then(function (r) {
                if (r.ok) {
                    localStorage.removeItem('frcastr-last-dash');
                    window.location.href = '/';
                } else {
                    r.text().then(function (t) { alert('Could not delete dashboard: ' + t); });
                }
            })
            .catch(function () { alert('Could not delete dashboard.'); });
    };

    function escHtml(s) {
        return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
