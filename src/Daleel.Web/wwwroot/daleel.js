// Small browser bridges used by Daleel pages.
window.daleel = {
    // Resolves the visitor's current coordinates via the Geolocation API.
    // Returns { lat, lng } or rejects with a message the C# side surfaces to the user.
    getLocation: function () {
        return new Promise(function (resolve, reject) {
            if (!('geolocation' in navigator)) {
                reject('Geolocation is not supported by this browser.');
                return;
            }

            navigator.geolocation.getCurrentPosition(
                function (pos) { resolve({ lat: pos.coords.latitude, lng: pos.coords.longitude }); },
                function (err) { reject(err.message || 'Location permission denied.'); },
                { enableHighAccuracy: false, timeout: 10000, maximumAge: 300000 });
        });
    },

    // Best-effort detection of the HaramBlur browser extension. The extension injects elements
    // and a global marker into the page; if any are present we hide the install banner.
    haramBlurInstalled: function () {
        try {
            if (window.__haramblur || window.haramBlur) {
                return true;
            }
            return document.querySelector(
                '[class*="haramblur" i],[id*="haramblur" i],[data-haramblur]') !== null;
        } catch (e) {
            return false;
        }
    },

    // Writes a cookie that survives reloads and browser restarts (default ~1 year). Used to
    // persist UI dismissals (e.g. the HaramBlur banner) so they don't reappear on every visit.
    setCookie: function (name, value, days) {
        try {
            const maxAge = (days || 365) * 24 * 60 * 60;
            document.cookie = name + '=' + encodeURIComponent(value) +
                ';path=/;max-age=' + maxAge + ';samesite=lax';
        } catch (e) {
            /* ignore */
        }
    },

    // Reads a cookie value by name, or returns an empty string if it is absent.
    getCookie: function (name) {
        try {
            const match = document.cookie.match('(?:^|; )' + name + '=([^;]*)');
            return match ? decodeURIComponent(match[1]) : '';
        } catch (e) {
            return '';
        }
    },

    // Triggers a client-side file download of the given text (used to export saved results).
    download: function (filename, text, mime) {
        const blob = new Blob([text], { type: mime || 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }
};

// Best-guess the visitor's market (2-letter country code) for first-visit defaulting:
//   1) the region subtag of the browser's preferred languages ("ar-JO" -> "JO"), then
//   2) a small timezone -> country fallback for the markets we support.
// Returns "" when nothing usable is found, so the app knows to ask the user instead of guessing.
window.daleelDetectMarket = function () {
    try {
        const langs = (navigator.languages && navigator.languages.length)
            ? navigator.languages
            : [navigator.language];
        for (const l of langs) {
            const m = /[-_]([A-Za-z]{2})$/.exec(l || "");
            if (m) return m[1].toUpperCase();
        }
        const tz = (Intl.DateTimeFormat().resolvedOptions().timeZone) || "";
        const tzMap = {
            "Asia/Amman": "JO", "Asia/Riyadh": "SA", "Asia/Dubai": "AE",
            "Asia/Abu_Dhabi": "AE", "Africa/Cairo": "EG"
        };
        if (tzMap[tz]) return tzMap[tz];
    } catch (e) { /* ignore */ }
    return "";
};

// ── Store map (Leaflet + OpenStreetMap, lazy-loaded from CDN) ────────────────
let _leafletPromise;
function _ensureLeaflet() {
    if (window.L) return Promise.resolve();
    if (!_leafletPromise) {
        _leafletPromise = new Promise((resolve, reject) => {
            const css = document.createElement("link");
            css.rel = "stylesheet";
            css.href = "https://unpkg.com/leaflet@1.9.4/dist/leaflet.css";
            document.head.appendChild(css);
            const js = document.createElement("script");
            js.src = "https://unpkg.com/leaflet@1.9.4/dist/leaflet.js";
            js.onload = () => resolve();
            js.onerror = reject;
            document.head.appendChild(js);
        });
    }
    return _leafletPromise;
}

// Ask the browser for the visitor's location. Resolves to {lat,lng} or null (denied/unavailable).
window.daleelGetLocation = function () {
    return new Promise((resolve) => {
        if (!navigator.geolocation) { resolve(null); return; }
        navigator.geolocation.getCurrentPosition(
            (p) => resolve({ lat: p.coords.latitude, lng: p.coords.longitude }),
            () => resolve(null),
            { enableHighAccuracy: false, timeout: 8000, maximumAge: 300000 }
        );
    });
};

const _daleelMaps = {};
// Render (or re-render) a store map. markers: [{lat,lng,name,address,url}]; user: {lat,lng}|null.
window.daleelRenderMap = async function (elId, markers, user) {
    try {
        await _ensureLeaflet();
    } catch (e) { return; }
    const el = document.getElementById(elId);
    if (!el || !window.L) return;

    if (_daleelMaps[elId]) { _daleelMaps[elId].remove(); delete _daleelMaps[elId]; }

    const pts = (markers || []).filter((m) => m && m.lat != null && m.lng != null);
    const center = user ? [user.lat, user.lng] : (pts.length ? [pts[0].lat, pts[0].lng] : [31.95, 35.93]);
    const map = L.map(el, { scrollWheelZoom: false }).setView(center, pts.length ? 11 : 6);
    L.tileLayer("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", {
        attribution: "© OpenStreetMap", maxZoom: 19
    }).addTo(map);

    const bounds = [];
    const esc = (s) => (s || "").replace(/[<>&"]/g, (c) => ({ "<": "&lt;", ">": "&gt;", "&": "&amp;", '"': "&quot;" }[c]));
    pts.forEach((m) => {
        const popup = "<b>" + esc(m.name) + "</b>" +
            (m.address ? "<br>" + esc(m.address) : "") +
            (m.url ? '<br><a href="' + esc(m.url) + '" target="_blank" rel="noopener">View store</a>' : "");
        L.marker([m.lat, m.lng]).addTo(map).bindPopup(popup);
        bounds.push([m.lat, m.lng]);
    });
    if (user) {
        L.circleMarker([user.lat, user.lng], { radius: 7, color: "#1976d2", fillColor: "#1976d2", fillOpacity: 0.9 })
            .addTo(map).bindPopup("You are here");
        bounds.push([user.lat, user.lng]);
    }
    if (bounds.length > 1) { map.fitBounds(bounds, { padding: [30, 30], maxZoom: 14 }); }
    _daleelMaps[elId] = map;
    setTimeout(() => map.invalidateSize(), 120);
};
