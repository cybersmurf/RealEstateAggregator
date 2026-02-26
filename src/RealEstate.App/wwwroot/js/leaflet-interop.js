/**
 * Leaflet + Blazor JSInterop bridge
 * Exposes window.leafletMap.* functions called from Map.razor
 */

window.leafletMap = (() => {
    const _maps = {};        // mapId → L.Map instance
    const _markerLayers = {}; // mapId → L.LayerGroup
    const _corridorLayers = {}; // mapId → L.LayerGroup

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

            if (dotNetRef) {
                marker.on('click', () => {
                    dotNetRef.invokeMethodAsync('OnMarkerClicked', p.id);
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
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    const _iconCache = {};

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

    return { init, setMarkers, drawCorridor, clearCorridor, fitMarkers, destroy };
})();
