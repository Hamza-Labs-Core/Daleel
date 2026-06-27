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

// ── Store map (Google Maps JavaScript API, lazy-loaded from CDN) ─────────────
// The browser key is injected by the host document (App.razor) as window.__daleelMapsApiKey.
// We load the Maps script only the first time a map is actually rendered, then reuse it.
let _gmapsPromise;
function _ensureGoogleMaps() {
    if (window.google && window.google.maps) return Promise.resolve();
    if (!_gmapsPromise) {
        _gmapsPromise = new Promise((resolve, reject) => {
            const key = window.__daleelMapsApiKey || "";
            // Google invokes this global once the API is ready (async/callback loading pattern).
            const cb = "__daleelGmapsReady";
            window[cb] = () => resolve();
            const js = document.createElement("script");
            // The marker library brings in google.maps.marker.AdvancedMarkerElement.
            js.src = "https://maps.googleapis.com/maps/api/js?key=" +
                encodeURIComponent(key) + "&loading=async&libraries=marker&callback=" + cb;
            js.async = true;
            js.onerror = reject;
            document.head.appendChild(js);
        });
    }
    return _gmapsPromise;
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
    try {
        await _ensureGoogleMaps();
    } catch (e) { return; }
    const el = document.getElementById(elId);
    if (!el || !(window.google && window.google.maps)) return;

    // Drop any prior map/listeners bound to this element before re-rendering.
    if (_daleelMaps[elId]) { delete _daleelMaps[elId]; }

    const pts = (markers || []).filter((m) => m && m.lat != null && m.lng != null);
    const center = user ? { lat: user.lat, lng: user.lng }
        : (pts.length ? { lat: pts[0].lat, lng: pts[0].lng } : { lat: 31.95, lng: 35.93 });
    const map = new google.maps.Map(el, {
        center,
        zoom: pts.length ? 11 : 6,
        // AdvancedMarkerElement requires a Map ID; the host may inject a cloud-styled one,
        // otherwise we fall back to Google's DEMO_MAP_ID so markers still render.
        mapId: window.__daleelMapsMapId || "DEMO_MAP_ID",
        scrollwheel: false,
        mapTypeControl: false,
        streetViewControl: false,
        fullscreenControl: false,
    });

    const bounds = new google.maps.LatLngBounds();
    let count = 0;
    const esc = (s) => (s || "").replace(/[<>&"]/g, (c) => ({ "<": "&lt;", ">": "&gt;", "&": "&amp;", '"': "&quot;" }[c]));
    const info = new google.maps.InfoWindow();
    pts.forEach((m) => {
        const position = { lat: m.lat, lng: m.lng };
        const marker = new google.maps.marker.AdvancedMarkerElement({
            position,
            map,
            title: m.name || "",
            gmpClickable: true,
        });
        const html = "<b>" + esc(m.name) + "</b>" +
            (m.address ? "<br>" + esc(m.address) : "") +
            (m.url ? '<br><a href="' + esc(m.url) + '" target="_blank" rel="noopener">View store</a>' : "");
        marker.addEventListener("gmp-click", () => { info.setContent(html); info.open({ anchor: marker, map }); });
        bounds.extend(position);
        count++;
    });
    if (user) {
        const userPos = { lat: user.lat, lng: user.lng };
        // The legacy SymbolPath.CIRCLE glyph becomes a styled DOM element for the advanced marker.
        const dot = document.createElement("div");
        dot.style.cssText =
            "width:14px;height:14px;border-radius:50%;background:#1976d2;" +
            "opacity:0.9;border:2px solid #ffffff;box-sizing:border-box;";
        const here = new google.maps.marker.AdvancedMarkerElement({
            position: userPos,
            map,
            title: "You are here",
            content: dot,
        });
        bounds.extend(userPos);
        count++;
    }
    if (count > 1) {
        map.fitBounds(bounds, 30);
        // Don't over-zoom when all pins are clustered tightly together.
        google.maps.event.addListenerOnce(map, "idle", () => { if (map.getZoom() > 14) map.setZoom(14); });
    }
    _daleelMaps[elId] = map;
    // The map often initializes inside a still-expanding panel; nudge it a few times so it re-measures
    // and stays centred once the container reaches its final size.
    [120, 400, 900].forEach((d) => setTimeout(() => {
        try {
            google.maps.event.trigger(map, "resize");
            if (count > 1) { map.fitBounds(bounds, 30); } else { map.setCenter(center); }
        } catch (e) {}
    }, d));
  } catch (e) {
    // Never throw back into the .NET interop call — that would tear down the Blazor circuit.
  }
};
