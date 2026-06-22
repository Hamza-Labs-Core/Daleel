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
    }
};
