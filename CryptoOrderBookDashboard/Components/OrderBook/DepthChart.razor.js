// Hover crosshair + tooltip for the depth chart, computed entirely in the browser from the SVG's
// data-curve attribute (see DepthChart.razor). The mouse position only picks a price; the USD
// value always comes from the curve at that price, using the same steps the chart is drawn with.
// All elements are created inside .depth-chart-overlay, which Blazor never renders into.

const STEPS = 120; // must match DepthCurve.Steps
const states = new WeakMap();

export function attach(plot) {
    if (!plot || states.has(plot)) {
        return;
    }

    const svg = plot.querySelector("svg");
    const overlay = plot.querySelector(".depth-chart-overlay");
    if (!svg || !overlay) {
        return;
    }

    const vline = el("div", "dc-vline");
    const hline = el("div", "dc-hline");
    const dot = el("div", "dc-dot");
    const tip = el("div", "dc-tip");
    overlay.append(vline, hline, dot, tip);

    const state = { svg, overlay, vline, hline, dot, tip, raw: null, curve: null, lastX: null };

    state.onMove = e => {
        state.lastX = e.clientX;
        render(state);
    };
    state.onLeave = () => {
        state.lastX = null;
        overlay.classList.remove("active");
    };

    // The chart refreshes about once a second; keep the tooltip live while the mouse rests.
    state.observer = new MutationObserver(() => {
        if (state.lastX !== null) {
            render(state);
        }
    });
    state.observer.observe(svg, { attributes: true, attributeFilter: ["data-curve"] });

    plot.addEventListener("pointermove", state.onMove);
    plot.addEventListener("pointerdown", state.onMove);
    plot.addEventListener("pointerleave", state.onLeave);
    states.set(plot, state);
}

export function detach(plot) {
    const state = plot && states.get(plot);
    if (!state) {
        return;
    }

    state.observer.disconnect();
    plot.removeEventListener("pointermove", state.onMove);
    plot.removeEventListener("pointerdown", state.onMove);
    plot.removeEventListener("pointerleave", state.onLeave);
    states.delete(plot);
}

function render(state) {
    const curve = readCurve(state);
    const rect = state.svg.getBoundingClientRect();
    if (!curve || rect.width === 0) {
        state.overlay.classList.remove("active");
        return;
    }

    const x = Math.min(Math.max(state.lastX - rect.left, 0), rect.width);
    const offset = x / rect.width - 0.5;          // -0.5 .. 0.5, 0 = mid
    const isBid = offset < 0;
    const distance = Math.abs(offset) * 2;        // 0 .. 1 of the band
    const index = Math.min(STEPS - 1, Math.floor(distance * STEPS));
    const values = isBid ? curve.bids : curve.asks;
    const covered = isBid ? curve.bidSteps : curve.askSteps;
    const pctFromMid = (isBid ? -1 : 1) * distance * curve.percent;
    const price = curve.mid * (1 + pctFromMid / 100);
    const hasData = index < covered;
    const value = hasData ? values[index] : null;

    state.overlay.classList.add("active");
    state.overlay.classList.toggle("bid", isBid);
    state.overlay.classList.toggle("ask", !isBid);
    state.vline.style.left = `${x}px`;

    if (hasData) {
        const y = rect.height * (1 - value / curve.scale);
        const midX = rect.width / 2;
        state.hline.style.display = "";
        state.dot.style.display = "";
        state.hline.style.top = `${y}px`;
        state.hline.style.left = `${Math.min(x, midX)}px`;
        state.hline.style.width = `${Math.abs(midX - x)}px`;
        state.dot.style.left = `${x}px`;
        state.dot.style.top = `${y}px`;
    } else {
        state.hline.style.display = "none";
        state.dot.style.display = "none";
    }

    const label = isBid ? "Bids to here" : "Asks to here";
    state.tip.innerHTML =
        `<div><span class="dc-k">Price</span> ${formatPrice(price, curve.mid)} ` +
        `<span class="dc-pct">(${pctFromMid >= 0 ? "+" : ""}${pctFromMid.toFixed(2)}%)</span></div>` +
        `<div><span class="dc-k">${label}</span> ` +
        `<span class="dc-v">${hasData ? formatUsd(value) : "no data"}</span></div>`;

    // Follow the mouse, flipping to the other side near the right edge and staying inside.
    const tipWidth = state.tip.offsetWidth;
    const tipHeight = state.tip.offsetHeight;
    const left = x + 12 + tipWidth > rect.width ? x - 12 - tipWidth : x + 12;
    const anchorY = hasData ? rect.height * (1 - value / curve.scale) : rect.height / 2;
    const top = Math.min(Math.max(anchorY - tipHeight - 8, 0), rect.height - tipHeight);
    state.tip.style.left = `${Math.max(0, left)}px`;
    state.tip.style.top = `${top}px`;
}

function readCurve(state) {
    const raw = state.svg.getAttribute("data-curve");
    if (raw === state.raw) {
        return state.curve;
    }

    state.raw = raw;
    state.curve = null;
    if (!raw) {
        return null;
    }

    const parts = raw.split(";");
    if (parts.length < 7) {
        return null;
    }

    const list = s => (s ? s.split(",").map(Number) : []);
    state.curve = {
        mid: Number(parts[0]),
        percent: Number(parts[1]),
        scale: Number(parts[2]) || 1,
        bidSteps: Number(parts[3]),
        askSteps: Number(parts[4]),
        bids: list(parts[5]),
        asks: list(parts[6]),
    };
    return state.curve;
}

// About 5 significant digits, but never fewer than 2 decimals (64,596.12 / 110.60 / 0.13310).
function formatPrice(price, mid) {
    const magnitude = Math.floor(Math.log10(Math.abs(mid) || 1));
    const decimals = Math.min(8, Math.max(2, 4 - magnitude));
    return price.toLocaleString("en-US", { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
}

// Same thresholds as DisplayFormat.CompactUsd on the server.
function formatUsd(value) {
    const abs = Math.abs(value);
    if (abs >= 999_995_000) return `$${(value / 1e9).toFixed(2)}B`;
    if (abs >= 999_950) return `$${(value / 1e6).toFixed(2)}M`;
    if (abs >= 999.5) return `$${(value / 1e3).toFixed(1)}k`;
    return `$${value.toFixed(0)}`;
}

function el(tag, className) {
    const node = document.createElement(tag);
    node.className = className;
    return node;
}
