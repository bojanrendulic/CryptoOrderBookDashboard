// Scrolls only the panel's own scroll container so the spread sits in its middle.
// (scrollIntoView would also scroll every ancestor, making the whole page jump.)
export function centerSpread(marker) {
    const container = marker?.closest(".order-book-columns");
    if (!container) {
        return;
    }

    const offset = marker.getBoundingClientRect().top - container.getBoundingClientRect().top;
    container.scrollTop += offset - container.clientHeight / 2;
}
