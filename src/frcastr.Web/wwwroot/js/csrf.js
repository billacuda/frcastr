// Antiforgery for the JSON APIs.
//
// The server validates an antiforgery token on every non-GET verb across all controllers, so a
// mutating call that does not carry one gets a 400. csrfFetch is a drop-in for fetch that attaches
// it — deliberately nothing more. The alternative was routing every call site through adminFetch,
// but that helper also alerts on failure and reloads on success, so adopting it wholesale would
// have rewritten the error handling of twenty call sites to solve a header problem.
//
// Loaded from _Layout ahead of every other script, so anything that fetches can use it.
(function () {
    'use strict';

    var SAFE_METHODS = ['GET', 'HEAD', 'OPTIONS', 'TRACE'];

    // Rendered into every page by _Layout. Absent on a page served without the layout, in which
    // case the request goes out untokenised and the server rejects it — a visible 400 rather than
    // a silent bypass.
    function csrfToken() {
        var meta = document.querySelector('meta[name="csrf-token"]');
        return meta ? meta.getAttribute('content') : null;
    }

    function csrfFetch(url, options) {
        var init = Object.assign({}, options || {});
        var method = (init.method || 'GET').toUpperCase();

        if (SAFE_METHODS.indexOf(method) < 0) {
            var token = csrfToken();
            if (token) {
                // Headers may arrive as a plain object or a Headers instance; normalise to the
                // former, which is what every call site in this app passes.
                var headers = {};
                if (init.headers instanceof Headers) {
                    init.headers.forEach(function (v, k) { headers[k] = v; });
                } else {
                    headers = Object.assign({}, init.headers);
                }
                headers['X-CSRF-TOKEN'] = token;
                init.headers = headers;
            }
        }

        return fetch(url, init);
    }

    window.csrfToken = csrfToken;
    window.csrfFetch = csrfFetch;
})();
