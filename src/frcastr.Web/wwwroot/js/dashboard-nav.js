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

        // A phone-layout choice for the dashboard being viewed. Only shown where it can be acted
        // on: the dashboard page (which is what defines frcastrConfig) and only to someone who
        // may save layouts.
        var cfg = window.frcastrConfig;
        if (cfg && cfg.isAuthenticated) {
            html += '<li><hr class="dropdown-divider"></li>' +
                '<li><label class="dropdown-item d-flex align-items-start gap-2 mb-0" for="mobileTwoColBox">' +
                '<input class="form-check-input mt-1 flex-shrink-0" type="checkbox" id="mobileTwoColBox"' +
                (cfg.mobileTwoColumn ? ' checked' : '') + ' />' +
                '<span>Two columns on phones' +
                '<span class="small text-body-secondary d-block" style="white-space:normal;max-width:15rem">' +
                'Widgets side by side here stay side by side on a phone; one alone in a row spans the width.' +
                '</span></span></label></li>';
        }

        menu.innerHTML = html;

        var twoColBox = document.getElementById('mobileTwoColBox');
        if (twoColBox) {
            twoColBox.addEventListener('change', onTwoColumnToggle);
            // Bootstrap closes the menu on any click inside it; a setting you may want to see the
            // effect of is worth keeping open.
            twoColBox.parentElement.addEventListener('click', function (e) { e.stopPropagation(); });
        }

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

    // Saved against the dashboard, then handed to the grid so a desktop browser narrow enough to
    // be in phone mode redraws immediately instead of on the next load.
    function onTwoColumnToggle(e) {
        var box  = e.target;
        var want = box.checked;
        box.disabled = true;

        csrfFetch('/api/dashboard/mobile-columns?dashboard=' + encodeURIComponent(getCurrentDash()) +
                  '&twoColumn=' + (want ? 'true' : 'false'), { method: 'POST' })
            .then(function (r) {
                if (!r.ok) throw new Error(r.status);
                if (window.frcastrConfig) window.frcastrConfig.mobileTwoColumn = want;
                window.dispatchEvent(new CustomEvent('mobileColumnsChanged', { detail: { twoColumn: want } }));
            })
            .catch(function () {
                box.checked = !want;
                alert('Could not save the phone column setting.');
            })
            .finally(function () { box.disabled = false; });
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
