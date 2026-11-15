const maps = new Map();

export async function fetchObservations(url) {
    try {
        const response = await fetch(new URL(url, window.location.href), {
            headers: { Accept: "application/json" }
        });
        return {
            success: response.ok,
            statusCode: response.status,
            body: await response.text()
        };
    } catch (error) {
        return {
            success: false,
            statusCode: 0,
            body: error instanceof Error ? error.message : String(error)
        };
    }
}

export function initialize(elementId) {
    const element = document.getElementById(elementId);
    if (!element || !window.L) {
        throw new Error("Leaflet did not load or the map element is missing.");
    }

    if (maps.has(elementId)) {
        maps.get(elementId).map.invalidateSize();
        return;
    }

    const map = window.L.map(elementId, {
        worldCopyJump: true,
        zoomControl: true
    }).setView([20, 0], 2);

    window.L.tileLayer("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", {
        maxZoom: 18,
        attribution: "&copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a> contributors"
    }).addTo(map);

    maps.set(elementId, {
        map,
        markers: window.L.layerGroup().addTo(map)
    });
    window.setTimeout(() => map.invalidateSize(), 100);
}

export function setObservations(observations) {
    const entry = maps.get("fire-map");
    if (!entry) {
        return;
    }

    entry.markers.clearLayers();
    const locations = [];
    for (const observation of observations) {
        const color = observation.instrument === "MODIS" ? "#ff6d43" : "#ffd166";
        const marker = window.L.circleMarker(
            [observation.latitude, observation.longitude],
            {
                radius: 5,
                color: "#15212b",
                weight: 1,
                fillColor: color,
                fillOpacity: 0.9
            });
        marker.bindPopup(createPopup(observation));
        marker.addTo(entry.markers);
        locations.push([observation.latitude, observation.longitude]);
    }

    if (locations.length === 1) {
        entry.map.setView(locations[0], Math.max(entry.map.getZoom(), 7));
    } else if (locations.length > 1) {
        entry.map.fitBounds(window.L.latLngBounds(locations), { padding: [32, 32], maxZoom: 9 });
    }
}

export function dispose(elementId) {
    const entry = maps.get(elementId);
    if (entry) {
        entry.map.remove();
        maps.delete(elementId);
    }
}

function createPopup(observation) {
    const content = document.createElement("div");
    content.className = "fire-popup";
    const title = document.createElement("strong");
    title.textContent = `${observation.satellite} · ${observation.instrument}`;
    const time = document.createElement("span");
    time.textContent = new Date(observation.acquiredAt).toISOString().replace("T", " ").replace(/\.\d{3}Z$/, " UTC");
    const details = document.createElement("span");
    details.textContent = `${observation.confidence} confidence · ${Number(observation.brightness).toFixed(1)} K · ${Number(observation.fireRadiativePower).toFixed(2)} MW`;
    const coordinates = document.createElement("span");
    coordinates.textContent = `${Number(observation.latitude).toFixed(4)}, ${Number(observation.longitude).toFixed(4)}`;
    content.append(title, time, details, coordinates);
    return content;
}
