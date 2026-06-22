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
