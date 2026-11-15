const maps = new Map();
const timeSeriesByElement = new Map();

export async function fetchObservations(url, append) {
    try {
        const response = await fetch(new URL(url, window.location.href), {
            headers: { Accept: "application/json" }
        });
        const body = await response.text();
        if (!response.ok) {
            return {
                success: false,
                statusCode: response.status,
                body: body.slice(0, 2000),
                items: [],
                next: null,
                totalCount: 0
            };
        }

        const page = JSON.parse(body);
        if (!Array.isArray(page.items) ||
            (page.next !== null && typeof page.next !== "object")) {
            throw new Error("The observation API returned an invalid response.");
        }

        const entry = maps.get("fire-map");
        if (!entry) {
            throw new Error("The observation map is not initialized.");
        }

        entry.observations = append
            ? entry.observations.concat(page.items)
            : page.items;
        renderObservations(entry);
        return {
            success: true,
            statusCode: response.status,
            body: "",
            items: page.items.slice(0, 12),
            next: page.next,
            totalCount: entry.observations.length
        };
    } catch (error) {
        return {
            success: false,
            statusCode: 0,
            body: error instanceof Error ? error.message : String(error),
            items: [],
            next: null,
            totalCount: 0
        };
    }
}

export async function fetchTimeSeries(elementId, url, scale) {
    try {
        const response = await fetch(new URL(url, window.location.href), {
            headers: { Accept: "application/json" }
        });
        const body = await response.text();
        if (!response.ok) {
            return {
                success: false,
                statusCode: response.status,
                body: body.slice(0, 2000),
                pointCount: 0
            };
        }

        const points = JSON.parse(body);
        if (!Array.isArray(points) || points.some(point =>
            typeof point.date !== "string" ||
            !Number.isFinite(point.modisCount) ||
            !Number.isFinite(point.viirsCount) ||
            point.modisCount < 0 ||
            point.viirsCount < 0)) {
            throw new Error("The time-series API returned an invalid response.");
        }

        const entry = { points, scale: normalizeScale(scale) };
        timeSeriesByElement.set(elementId, entry);
        renderTimeSeries(elementId, entry);
        return {
            success: true,
            statusCode: response.status,
            body: "",
            pointCount: points.length
        };
    } catch (error) {
        return {
            success: false,
            statusCode: 0,
            body: error instanceof Error ? error.message : String(error),
            pointCount: 0
        };
    }
}

export function setTimeSeriesScale(elementId, scale) {
    const entry = timeSeriesByElement.get(elementId);
    if (!entry) {
        throw new Error("No time-series data is loaded for the requested chart.");
    }

    entry.scale = normalizeScale(scale);
    renderTimeSeries(elementId, entry);
}

export function clearTimeSeries(elementId) {
    timeSeriesByElement.delete(elementId);
    document.getElementById(elementId)?.replaceChildren();
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
        markers: window.L.layerGroup().addTo(map),
        observations: []
    });
    window.setTimeout(() => map.invalidateSize(), 100);
}

export function clearObservations() {
    const entry = maps.get("fire-map");
    if (!entry) {
        return;
    }

    entry.observations = [];
    entry.markers.clearLayers();
}

function renderObservations(entry) {
    const observations = entry.observations;
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

function normalizeScale(scale) {
    return Number.isFinite(scale) && scale >= 0 ? scale : 5;
}

function renderTimeSeries(elementId, entry) {
    const container = document.getElementById(elementId);
    if (!container) {
        throw new Error("The time-series chart element is missing.");
    }

    const namespace = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(namespace, "svg");
    svg.setAttribute("class", "time-series-chart");
    svg.setAttribute("viewBox", "0 0 1000 300");
    svg.setAttribute("role", "img");
    svg.setAttribute(
        "aria-label",
        `Daily MODIS detections and VIIRS detections scaled by ${entry.scale}`);

    const title = document.createElementNS(namespace, "title");
    title.textContent = "Daily observation counts; VIIRS scaled client-side.";
    svg.append(title);

    const maximum = entry.points.reduce(
        (current, point) => Math.max(current, point.modisCount, point.viirsCount * entry.scale),
        1);
    appendSvgElement(svg, "line", { x1: 36, y1: 30, x2: 980, y2: 30, class: "chart-grid" });
    appendSvgElement(svg, "line", { x1: 36, y1: 135, x2: 980, y2: 135, class: "chart-grid" });
    appendSvgElement(svg, "line", { x1: 36, y1: 240, x2: 980, y2: 240, class: "chart-axis" });
    appendSvgElement(
        svg,
        "text",
        { x: 30, y: 35, "text-anchor": "end", class: "chart-label" },
        new Intl.NumberFormat().format(maximum));
    appendSvgElement(
        svg,
        "text",
        { x: 30, y: 245, "text-anchor": "end", class: "chart-label" },
        "0");
    appendSvgElement(
        svg,
        "polyline",
        { points: makeChartPath(entry.points, maximum, false), class: "chart-modis" });
    appendSvgElement(
        svg,
        "polyline",
        { points: makeChartPath(entry.points, maximum, true, entry.scale), class: "chart-viirs" });
    appendSvgElement(
        svg,
        "text",
        { x: 36, y: 276, class: "chart-label" },
        entry.points[0]?.date ?? "");
    appendSvgElement(
        svg,
        "text",
        { x: 980, y: 276, "text-anchor": "end", class: "chart-label" },
        entry.points.at(-1)?.date ?? "");

    container.replaceChildren(svg);
}

function makeChartPath(points, maximum, viirs, scale = 1) {
    const lastIndex = points.length - 1;
    return points.map((point, index) => {
        const x = lastIndex === 0 ? 508 : 36 + 944 * index / lastIndex;
        const count = viirs ? point.viirsCount * scale : point.modisCount;
        const y = 240 - 210 * count / maximum;
        return `${x.toFixed(2)},${y.toFixed(2)}`;
    }).join(" ");
}

function appendSvgElement(svg, name, attributes, text = null) {
    const element = document.createElementNS("http://www.w3.org/2000/svg", name);
    for (const [attribute, value] of Object.entries(attributes)) {
        element.setAttribute(attribute, value);
    }
    if (text !== null) {
        element.textContent = text;
    }
    svg.append(element);
}
