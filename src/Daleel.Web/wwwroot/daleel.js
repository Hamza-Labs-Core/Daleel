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
