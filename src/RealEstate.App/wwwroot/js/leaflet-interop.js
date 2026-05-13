/**
 * Leaflet + Blazor JSInterop bridge
 * Exposes window.leafletMap.* functions called from Map.razor
 */

window.leafletMap = (() => {
    const _maps = {};        // mapId → L.Map instance
    const _markerLayers = {}; // mapId → L.LayerGroup
    const _corridorLayers = {}; // mapId → L.LayerGroup
    const _bboxLayers = {}; // mapId → L.LayerGroup (bbox rectangle select)
    const _markerById = {}; // mapId → { listingId → { marker, point } }

    /**
     * Inicializuje Leaflet mapu v elementu s daným ID.
     * Leaflet je načten lokálně (wwwroot/lib/leaflet/leaflet.js) – L je vždy dostupné.
     * @param {string} mapId - ID HTML elementu
     * @param {number} centerLat - Defaultní střed (lat)
     * @param {number} centerLon - Defaultní střed (lon)
     * @param {number} zoom - Defaultní zoom
     */
    function init(mapId, centerLat = 49.0, centerLon = 16.6, zoom = 8) {
        if (_maps[mapId]) {
            _maps[mapId].remove();
        }

        const map = L.map(mapId, { preferCanvas: true }).setView([centerLat, centerLon], zoom);

        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
            maxZoom: 18
        }).addTo(map);

        _maps[mapId] = map;
        _markerLayers[mapId] = L.layerGroup().addTo(map);
        _corridorLayers[mapId] = L.layerGroup().addTo(map);
        _bboxLayers[mapId] = L.layerGroup().addTo(map);

        return true;
    }

    /**
     * Přidá markery inzerátů na mapu.
     * @param {string} mapId
     * @param {Array} points - [{id, title, price, locationText, latitude, longitude, propertyType, offerType, mainPhotoUrl, sourceCode}]
     * @param {object} dotNetRef - Reference na Blazor komponentu pro callback při kliknutí
     */
    function setMarkers(mapId, points, dotNetRef) {
        const layer = _markerLayers[mapId];
        if (!layer) return;

        layer.clearLayers();
        _markerById[mapId] = {};

        const priceFormatter = new Intl.NumberFormat('cs-CZ', { style: 'currency', currency: 'CZK', maximumFractionDigits: 0 });

        for (const p of points) {
            const icon = getMarkerIcon(p.propertyType, p.offerType);
            const marker = L.marker([p.latitude, p.longitude], { icon });

            const priceStr = p.price ? priceFormatter.format(p.price) : 'Cena neuvedena';
            const popupHtml = `
                <div style="min-width:220px;max-width:280px">
                    ${p.mainPhotoUrl ? `<img src="${escapeHtml(p.mainPhotoUrl)}" style="width:100%;height:130px;object-fit:cover;border-radius:4px;margin-bottom:6px" onerror="this.style.display='none'">` : ''}
                    <b style="font-size:13px">${escapeHtml(p.title)}</b><br>
                    <span style="color:#1976d2;font-weight:bold">${priceStr}</span><br>
                    <small>${escapeHtml(p.locationText)}</small><br>
                    <small style="color:#888">${escapeHtml(p.sourceCode)} · ${escapeHtml(p.propertyType)} · ${escapeHtml(p.offerType)}</small><br>
                    <a href="/listing/${p.id}" target="_blank" style="font-size:12px">Otevřít inzerát ↗</a>
                </div>`;

            marker.bindPopup(popupHtml);

            _markerById[mapId][p.id] = { marker, point: p };

            if (dotNetRef) {
                marker.on('click', () => {
                    dotNetRef.invokeMethodAsync('OnMarkerClicked', p.id);
                });
                marker.on('mouseover', () => {
                    dotNetRef.invokeMethodAsync('OnMarkerHovered', p.id);
                });
                marker.on('mouseout', () => {
                    dotNetRef.invokeMethodAsync('OnMarkerHovered', null);
                });
            }

            layer.addLayer(marker);
        }
    }

    /**
     * Vykreslí koridor (WKT polygon nebo POLYGON text) na mapě a přiblíží na něj.
     * @param {string} mapId
     * @param {string} wkt - WKT POLYGON nebo MULTIPOLYGON
     */
    function drawCorridor(mapId, wkt) {
        const layer = _corridorLayers[mapId];
        const map = _maps[mapId];
        if (!layer || !map) return;

        layer.clearLayers();

        try {
            const latlngs = parseWktPolygon(wkt);
            if (!latlngs) return;

            const polygon = L.polygon(latlngs, {
                color: '#1976d2',
                fillColor: '#42a5f5',
                fillOpacity: 0.15,
                weight: 2,
                dashArray: '6,4'
            }).addTo(layer);

            map.fitBounds(polygon.getBounds(), { padding: [20, 20] });
        } catch (e) {
            console.warn('Nelze vykreslit koridor:', e);
        }
    }

    /** Odstraní koridor z mapy. */
    function clearCorridor(mapId) {
        const layer = _corridorLayers[mapId];
        if (layer) layer.clearLayers();
    }

    /** Přiblíží mapu na markery. */
    function fitMarkers(mapId) {
        const layer = _markerLayers[mapId];
        const map = _maps[mapId];
        if (!layer || !map) return;

        const bounds = L.featureGroup(layer.getLayers()).getBounds();
        if (bounds.isValid()) {
            map.fitBounds(bounds, { padding: [40, 40] });
        }
    }

    /** Správně zničí mapu (pro dispose pattern). */
    function destroy(mapId) {
        if (_maps[mapId]) {
            _maps[mapId].remove();
            delete _maps[mapId];
            delete _markerLayers[mapId];
            delete _corridorLayers[mapId];
            delete _bboxLayers[mapId];
            delete _markerById[mapId];
        }
    }

    /**
     * Zapne režim výběru obdélníkové oblasti (bbox).
     * Uživatel táhne myší po mapě – výsledný bbox je odeslán do Blazor přes dotNetRef.
     * @param {string} mapId
     * @param {object} dotNetRef – DotNetObjectReference s metodou OnBboxSelected(latMin, lonMin, latMax, lonMax)
     */
    function enableBboxSelect(mapId, dotNetRef) {
        const map = _maps[mapId];
        if (!map) return;

        // Styl kurzoru
        map.getContainer().style.cursor = 'crosshair';

        let startLatLng = null;
        let bboxRect = null;
        const layer = _bboxLayers[mapId];

        function onMouseDown(e) {
            startLatLng = e.latlng;
            layer.clearLayers();
            // Zabrání posunu mapy při tažení
            map.dragging.disable();
            map.on('mousemove', onMouseMove);
            map.on('mouseup', onMouseUp);
        }

        function onMouseMove(e) {
            if (!startLatLng) return;
            if (bboxRect) layer.removeLayer(bboxRect);
            const bounds = L.latLngBounds(startLatLng, e.latlng);
            bboxRect = L.rectangle(bounds, {
                color: '#ff7043',
                weight: 2,
                fillOpacity: 0.12,
                dashArray: '6,3'
            }).addTo(layer);
        }

        function onMouseUp(e) {
            map.dragging.enable();
            map.off('mousemove', onMouseMove);
            map.off('mouseup', onMouseUp);
            map.off('mousedown', onMouseDown);
            map.getContainer().style.cursor = '';

            if (!startLatLng) return;
            const endLatLng = e.latlng;
            const bounds = L.latLngBounds(startLatLng, endLatLng);

            // Minimální velikost – zabrání náhodnému kliknutí
            const sw = bounds.getSouthWest();
            const ne = bounds.getNorthEast();
            if (Math.abs(ne.lat - sw.lat) < 0.005 || Math.abs(ne.lng - sw.lng) < 0.005) {
                layer.clearLayers();
                startLatLng = null;
                return;
            }

            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnBboxSelected',
                    sw.lat, sw.lng, ne.lat, ne.lng
                );
            }
            startLatLng = null;
        }

        map.on('mousedown', onMouseDown);
        // Uložíme handler pro disable
        map._bboxDownHandler = onMouseDown;
    }

    /**
     * Vypne režim výběru bbox a vymaže nakreslenou oblast.
     */
    function disableBboxSelect(mapId) {
        const map = _maps[mapId];
        if (!map) return;
        if (map._bboxDownHandler) {
            map.off('mousedown', map._bboxDownHandler);
            delete map._bboxDownHandler;
        }
        map.dragging.enable();
        map.getContainer().style.cursor = '';
        if (_bboxLayers[mapId]) _bboxLayers[mapId].clearLayers();
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    const _iconCache = {};
    const _highlightIconCache = {};

    function getMarkerIcon(propertyType, offerType) {
        const key = `${propertyType}_${offerType}`;
        if (_iconCache[key]) return _iconCache[key];

        const colorMap = {
            'House_Sale': '#e53935',
            'House_Rent': '#ff7043',
            'Apartment_Sale': '#1e88e5',
            'Apartment_Rent': '#42a5f5',
            'Land_Sale': '#43a047',
            'Land_Rent': '#66bb6a',
            'Cottage_Sale': '#8e24aa',
            'Cottage_Rent': '#ab47bc',
            'Commercial_Sale': '#f4511e',
            'Commercial_Rent': '#fb8c00',
        };

        const color = colorMap[key] || '#757575';
        const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 36" width="24" height="36">
            <path d="M12 0C5.4 0 0 5.4 0 12c0 9 12 24 12 24s12-15 12-24c0-6.6-5.4-12-12-12z"
                  fill="${color}" stroke="white" stroke-width="1.5"/>
            <circle cx="12" cy="12" r="5" fill="white" fill-opacity="0.9"/>
        </svg>`;

        const icon = L.divIcon({
            html: svg,
            className: '',
            iconSize: [24, 36],
            iconAnchor: [12, 36],
            popupAnchor: [0, -36]
        });

        _iconCache[key] = icon;
        return icon;
    }

    function getHighlightedMarkerIcon(propertyType, offerType) {
        const key = `${propertyType}_${offerType}`;
        if (_highlightIconCache[key]) return _highlightIconCache[key];

        const colorMap = {
            'House_Sale': '#e53935',
            'House_Rent': '#ff7043',
            'Apartment_Sale': '#1e88e5',
            'Apartment_Rent': '#42a5f5',
            'Land_Sale': '#43a047',
            'Land_Rent': '#66bb6a',
            'Cottage_Sale': '#8e24aa',
            'Cottage_Rent': '#ab47bc',
            'Commercial_Sale': '#f4511e',
            'Commercial_Rent': '#fb8c00',
        };

        const color = colorMap[key] || '#757575';
        const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 48" width="32" height="48">
            <circle cx="16" cy="13" r="15" fill="${color}" fill-opacity="0.22" stroke="${color}" stroke-width="1.5"/>
            <path d="M16 1C9.4 1 4 6.4 4 13c0 10 12 22 12 22s12-12 12-22c0-6.6-5.4-12-12-12z"
                  fill="${color}" stroke="white" stroke-width="2"/>
            <circle cx="16" cy="13" r="6" fill="white" fill-opacity="0.95"/>
        </svg>`;

        const icon = L.divIcon({
            html: svg,
            className: '',
            iconSize: [32, 48],
            iconAnchor: [16, 48],
            popupAnchor: [0, -48]
        });

        _highlightIconCache[key] = icon;
        return icon;
    }

    /**
     * Zvýrazní marker inzerátu (při hoveru na kartě).
     */
    function highlightMarker(mapId, listingId) {
        const entry = _markerById[mapId]?.[listingId];
        if (!entry) return;
        const { marker, point } = entry;
        marker.setIcon(getHighlightedMarkerIcon(point.propertyType, point.offerType));
        marker.setZIndexOffset(1000);
    }

    /**
     * Zruší zvýraznění markeru.
     */
    function unhighlightMarker(mapId, listingId) {
        const entry = _markerById[mapId]?.[listingId];
        if (!entry) return;
        const { marker, point } = entry;
        marker.setIcon(getMarkerIcon(point.propertyType, point.offerType));
        marker.setZIndexOffset(0);
    }

    /**
     * Posune seznam karet na kartu daného inzerátu.
     */
    function scrollCardIntoView(listingId) {
        const el = document.getElementById('map-card-' + listingId);
        if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }

    /**
     * Parsuje WKT POLYGON nebo MULTIPOLYGON na Leaflet LatLng pole.
     * Vrátí L.LatLng[][] nebo null.
     */
    function parseWktPolygon(wkt) {
        if (!wkt) return null;
        wkt = wkt.trim();

        let coordStr;

        if (wkt.startsWith('MULTIPOLYGON')) {
            // MULTIPOLYGON(((lon lat, ...)), ((lon lat, ...)))
            // Vezmi první polygon
            const match = wkt.match(/MULTIPOLYGON\s*\(\s*\(\s*\(([^)]+)\)/);
            if (!match) return null;
            coordStr = match[1];
        } else if (wkt.startsWith('POLYGON')) {
            // POLYGON((lon lat, ...))
            const match = wkt.match(/POLYGON\s*\(\s*\(([^)]+)\)/);
            if (!match) return null;
            coordStr = match[1];
        } else {
            return null;
        }

        const latlngs = coordStr.trim().split(',').map(pair => {
            const [lonStr, latStr] = pair.trim().split(/\s+/);
            return [parseFloat(latStr), parseFloat(lonStr)];
        });

        return [latlngs];
    }

    function escapeHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    /**
     * Zapne automatický filtr inzerátů dle aktuálních hranic mapy (pan/zoom).
     * Při každém 'moveend' zavolá dotNetRef.OnMapBoundsChanged(latMin, lonMin, latMax, lonMax).
     * @param {string} mapId
     * @param {object} dotNetRef
     */
    function enableBoundsFilter(mapId, dotNetRef) {
        const map = _maps[mapId];
        if (!map) return;
        disableBoundsFilter(mapId); // odstraní předchozí listener
        function onMoveEnd() {
            const b = map.getBounds();
            const sw = b.getSouthWest();
            const ne = b.getNorthEast();
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnMapBoundsChanged', sw.lat, sw.lng, ne.lat, ne.lng);
            }
        }
        map._boundsFilterHandler = onMoveEnd;
        map.on('moveend', onMoveEnd);
        // Ihned spustit pro počáteční pohled
        onMoveEnd();
    }

    function disableBoundsFilter(mapId) {
        const map = _maps[mapId];
        if (!map || !map._boundsFilterHandler) return;
        map.off('moveend', map._boundsFilterHandler);
        map._boundsFilterHandler = null;
    }

    return { init, setMarkers, drawCorridor, clearCorridor, fitMarkers, destroy, enableBboxSelect, disableBboxSelect, enableBoundsFilter, disableBoundsFilter, highlightMarker, unhighlightMarker, scrollCardIntoView };
})();

/**
 * Triggers a file download in the browser from a base64-encoded byte array.
 * Called from Blazor server-side to deliver files fetched via internal HttpClient.
 */
window.downloadFile = function (filename, contentType, base64) {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const blob = new Blob([bytes], { type: contentType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};
