// Shared base map for the Radar page and the radar widget: OpenFreeMap vector tiles (no key, no
// usage cap) drawn by MapLibre GL inside Leaflet, following the Bootstrap theme. Falls back to
// OpenStreetMap raster tiles if the MapLibre scripts didn't load, so the map is never blank.
(function () {
    'use strict';

    var STYLE_LIGHT = 'https://tiles.openfreemap.org/styles/positron';
    var STYLE_DARK  = 'https://tiles.openfreemap.org/styles/dark';

    function isDark() {
        var html = document.getElementById('htmlRoot');
        return !!html && html.getAttribute('data-bs-theme') === 'dark';
    }

    /**
     * Adds the base layer to a Leaflet map and keeps it in step with the theme.
     * @param {L.Map} map
     * @param {L.Control.Attribution} attr  attribution control to credit the tiles in
     * @returns {{ layer: L.Layer, disconnect: function }} the layer, and a function that stops
     *          following the theme (call it when the map is removed)
     */
    window.frcastrBaseMap = function (map, attr) {
        var layer, observer = null;
        var useVector = !!(L.maplibreGL && window.maplibregl);

        if (useVector) {
            layer = L.maplibreGL({ style: isDark() ? STYLE_DARK : STYLE_LIGHT }).addTo(map);
            attr.addAttribution('<a href="https://openfreemap.org" target="_blank" rel="noopener noreferrer">OpenFreeMap</a> '
                + '&copy; <a href="https://www.openmaptiles.org/" target="_blank" rel="noopener noreferrer">OpenMapTiles</a> '
                + 'Data from <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener noreferrer">OpenStreetMap</a>');
        } else {
            layer = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19 }).addTo(map);
            layer.bringToBack();
            attr.addAttribution('&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener noreferrer">OpenStreetMap</a>');
        }

        var html = document.getElementById('htmlRoot');
        if (html && useVector) {
            observer = new MutationObserver(function () {
                var gl = layer.getMaplibreMap && layer.getMaplibreMap();
                if (gl) gl.setStyle(isDark() ? STYLE_DARK : STYLE_LIGHT);
            });
            observer.observe(html, { attributes: true, attributeFilter: ['data-bs-theme'] });
        }

        return {
            layer: layer,
            disconnect: function () { if (observer) { observer.disconnect(); observer = null; } }
        };
    };
})();
