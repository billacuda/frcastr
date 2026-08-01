// Server-rendered timestamps are stored in UTC, so formatting them on the server shows UTC to
// every viewer — an evening event lands on tomorrow's date. The server emits the raw UTC instant
// in data-utc instead and the browser formats it in its own local time.
//
//   <span data-utc="2026-07-31T22:15:00Z">07/31 22:15</span>   -> 07/31 18:15 (browser local)
//   <span data-utc="..." data-utc-format="date"></span>        -> date only
//
// The element's text is replaced and its title set to the full local timestamp. Elements with no
// data-utc (a null timestamp renders the attribute away) keep whatever the server wrote, so the
// server-rendered text doubles as the no-JavaScript fallback. Call localTime.apply(container)
// again after injecting markup dynamically.
(function () {
    var formats = {
        datetime:     { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' },
        datetimefull: { year: 'numeric', month: '2-digit', day: '2-digit',
                        hour: '2-digit', minute: '2-digit' },
        datetimesec:  { year: 'numeric', month: '2-digit', day: '2-digit',
                        hour: '2-digit', minute: '2-digit', second: '2-digit' },
        date:         { year: 'numeric', month: '2-digit', day: '2-digit' },
        time:         { hour: '2-digit', minute: '2-digit' }
    };

    function optionsFor(style) {
        return formats[(style || '').toLowerCase().replace(/[^a-z]/g, '')] || formats.datetime;
    }

    function format(iso, style) {
        var d = new Date(iso);
        if (isNaN(d.getTime())) return iso;
        return d.toLocaleString(undefined, optionsFor(style));
    }

    function apply(root) {
        (root || document).querySelectorAll('[data-utc]').forEach(function (el) {
            var iso = el.dataset.utc;
            if (!iso) return;
            var d = new Date(iso);
            if (isNaN(d.getTime())) return;
            el.textContent = d.toLocaleString(undefined, optionsFor(el.dataset.utcFormat));
            el.title = d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'long' });
        });
    }

    window.localTime = { format: format, apply: apply };

    if (document.readyState === 'loading')
        document.addEventListener('DOMContentLoaded', function () { apply(); });
    else
        apply();
})();
