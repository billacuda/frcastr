// Shared helper for admin actions that mutate and then reload.
//
// Every one of them used to be `await fetch(...); location.reload();` with nothing checking the
// response, so a rejected delete or a failed toggle looked exactly like a success — the page
// reloaded and simply showed the unchanged row. adminFetch makes the call, surfaces whatever the
// server said when it fails, and reloads only when the action actually worked.
(function () {
    'use strict';

    // Failures come back as JSON (an anonymous object with `message`, or a ProblemDetails) or as
    // plain text, depending on where in the pipeline they were raised. Take the most useful line
    // either way rather than showing a raw body.
    async function serverMessage(response) {
        var text = '';
        try { text = await response.text(); } catch (e) { /* body gone or already read */ }
        if (!text) return 'HTTP ' + response.status + ' ' + (response.statusText || '');
        try {
            var body = JSON.parse(text);
            return body.message || body.detail || body.title || text;
        } catch (e) {
            return text;
        }
    }

    // url     - the endpoint
    // options - fetch options, plus two of our own:
    //           reload: false  leave the page alone on success (the caller renders the result)
    //           quiet: [409]   status codes handed back instead of alerted, for callers that
    //                          answer them themselves
    // Returns the Response, so a caller can read its body. Callers that pass `quiet` must consume
    // the body themselves; otherwise it has already been read for the message.
    async function adminFetch(url, options) {
        var opts = options || {};
        var quiet = opts.quiet || [];

        var init = Object.assign({}, opts);
        delete init.reload;
        delete init.quiet;

        var response;
        try {
            // csrfFetch, not fetch: the server rejects an untokenised mutating call. See csrf.js.
            response = await csrfFetch(url, init);
        } catch (e) {
            // The network failed, or the app went away mid-request.
            alert('Request failed: ' + e);
            throw e;
        }

        if (response.ok) {
            if (opts.reload !== false) location.reload();
            return response;
        }

        if (quiet.indexOf(response.status) >= 0) return response;

        alert('Error: ' + await serverMessage(response));
        return response;
    }

    window.adminFetch = adminFetch;
    window.adminServerMessage = serverMessage;
})();
