(() => {
    const instances = new WeakMap();
    const padding = 40;
    const minScale = 0.25;
    const maxScale = 2.4;
    const dragThreshold = 4;

    function clamp(value, min, max) {
        return Math.max(min, Math.min(value, max));
    }

    function getInstance(viewport) {
        return instances.get(viewport);
    }

    // Keep full titles separate on narrow canvases or when inline titles collide.
    function arrangeLabels(instance) {
        const svg = instance.content.querySelector(".graph-view-svg");
        if (!svg) {
            return;
        }
        instance.contentObserver?.disconnect();
        try {
            for (const leader of svg.querySelectorAll(".graph-label-leader")) {
                leader.remove();
            }
            const labels = [...svg.querySelectorAll(".graph-node-title")];
            for (const label of labels) {
                const original = instance.labelTransforms.get(label);
                if (original !== undefined) {
                    if (original === null) {
                        label.removeAttribute("transform");
                    }
                    else {
                        label.setAttribute("transform", original);
                    }
                }
            }
            if (labels.length < 2) {
                return;
            }
            const inverse = svg.getScreenCTM()?.inverse();
            if (!inverse) {
                return;
            }
            const rows = [];
            for (const label of labels) {
                const node = label.closest(".graph-view-node");
                const circle = node?.querySelector("circle");
                const style = getComputedStyle(label);
                if (!circle || style.display === "none" || style.visibility === "hidden") {
                    continue;
                }
                const box = label.getBBox();
                const matrix = label.getScreenCTM();
                const circleMatrix = circle.getScreenCTM();
                if (!matrix || !circleMatrix) {
                    continue;
                }
                const transform = inverse.multiply(matrix);
                const origin = new DOMPoint(box.x, box.y).matrixTransform(transform);
                const corner = new DOMPoint(box.x + box.width, box.y + box.height).matrixTransform(transform);
                const center = new DOMPoint(circle.cx.baseVal.value, circle.cy.baseVal.value)
                    .matrixTransform(inverse.multiply(circleMatrix));
                const radius = circle.r.baseVal.value;
                rows.push({ label, node, origin, center, radius, width: Math.abs(corner.x - origin.x), height: Math.abs(corner.y - origin.y), transform });
                if (!instance.labelTransforms.has(label)) {
                    instance.labelTransforms.set(label, label.getAttribute("transform"));
                }
            }
            if (rows.length < 2) {
                return;
            }
            const collides = rows.some((a, i) => rows.slice(i + 1).some(b =>
                a.origin.x < b.origin.x + b.width + 4 && a.origin.x + a.width + 4 > b.origin.x &&
                a.origin.y < b.origin.y + b.height + 4 && a.origin.y + a.height + 4 > b.origin.y));
            if (instance.viewport.clientWidth > 760 && !collides) {
                return;
            }
            rows.sort((a, b) => a.center.y - b.center.y || a.center.x - b.center.x);
            const gap = 8;
            const x = Math.max(...rows.map(row => row.center.x + row.radius)) + 24;
            const totalHeight = rows.reduce((sum, row) => sum + row.height, 0) + gap * (rows.length - 1);
            let y = (rows[0].center.y + rows[rows.length - 1].center.y - totalHeight) / 2;
            for (const row of rows) {
                const from = new DOMPoint(x, y).matrixTransform(row.transform.inverse());
                const box = row.label.getBBox();
                const shift = `translate(${from.x - box.x} ${from.y - box.y})`;
                const original = instance.labelTransforms.get(row.label);
                row.label.setAttribute("transform", original ? `${original} ${shift}` : shift);
                const leader = document.createElementNS("http://www.w3.org/2000/svg", "line");
                leader.setAttribute("class", "graph-label-leader");
                leader.setAttribute("aria-hidden", "true");
                const parentInverse = row.node.getScreenCTM().inverse();
                const svgMatrix = svg.getScreenCTM();
                const start = new DOMPoint(row.center.x + row.radius, row.center.y).matrixTransform(svgMatrix).matrixTransform(parentInverse);
                const end = new DOMPoint(x - 5, y + row.height / 2).matrixTransform(svgMatrix).matrixTransform(parentInverse);
                leader.setAttribute("x1", start.x);
                leader.setAttribute("y1", start.y);
                leader.setAttribute("x2", end.x);
                leader.setAttribute("y2", end.y);
                leader.style.cssText = "stroke:currentColor;stroke-width:1;opacity:.35;pointer-events:none";
                row.node.insertBefore(leader, row.label);
                y += row.height + gap;
            }
        }
        finally {
            instance.contentObserver?.observe(instance.content, { subtree: true, childList: true, characterData: true, attributes: true });
        }
    }

    function getContentMetrics(instance) {
        const fallback = {
            x: 0,
            y: 0,
            width: Math.max(instance.content.offsetWidth || 0, 1),
            height: Math.max(instance.content.offsetHeight || 0, 1)
        };
        const svg = instance.content.querySelector(".graph-view-svg");
        if (!svg) {
            return fallback;
        }

        // Measure painted semantic content, not the layout canvas or invisible hit targets.
        // Screen matrices cancel the current pan/zoom and include nested SVG transforms.
        try {
            const svgMatrix = svg.getScreenCTM();
            if (!svgMatrix) {
                return fallback;
            }
            const inverse = svgMatrix.inverse();
            const points = [];
            for (const element of svg.querySelectorAll(".graph-edge, .graph-label-leader, .graph-view-node circle, .graph-view-node text")) {
                const style = getComputedStyle(element);
                if (style.display === "none" || style.visibility === "hidden") {
                    continue;
                }
                const matrix = element.getScreenCTM();
                if (!matrix) {
                    continue;
                }
                const box = element.getBBox();
                const stroke = style.stroke === "none" ? 0 : (parseFloat(style.strokeWidth) || 0) / 2;
                const transform = inverse.multiply(matrix);
                for (const x of [box.x - stroke, box.x + box.width + stroke]) {
                    for (const y of [box.y - stroke, box.y + box.height + stroke]) {
                        points.push(new DOMPoint(x, y).matrixTransform(transform));
                    }
                }
            }
            if (!points.length || points.some(point => !Number.isFinite(point.x) || !Number.isFinite(point.y))) {
                return fallback;
            }
            const x = Math.min(...points.map(point => point.x));
            const y = Math.min(...points.map(point => point.y));
            const width = Math.max(...points.map(point => point.x)) - x;
            const height = Math.max(...points.map(point => point.y)) - y;
            return width > 0 && height > 0 ? { x, y, width, height } : fallback;
        }
        catch {
            return fallback;
        }
    }

    function fitScale(instance, metrics) {
        const width = Math.max(instance.viewport.clientWidth, 1);
        const height = Math.max(instance.viewport.clientHeight, 1);
        const inset = Math.min(width < 480 ? 16 : padding, width / 4, height / 4);
        // Fit may be below the interactive zoom floor: every label and edge must still fit.
        return Math.min((width - inset * 2) / metrics.width, (height - inset * 2) / metrics.height, maxScale);
    }

    function publishState(instance) {
        const { viewport, state } = instance;
        viewport.dataset.scale = state.scale.toFixed(3);
        viewport.dataset.panX = state.panX.toFixed(1);
        viewport.dataset.panY = state.panY.toFixed(1);

        const zoomChip = viewport.closest(".graph-canvas-panel")?.querySelector("[data-graph-zoom-chip]");
        if (zoomChip) {
            zoomChip.textContent = `Zoom ${state.scale.toFixed(2)}x`;
        }
    }

    function applyTransform(instance, publish = true) {
        const { content, state } = instance;
        content.style.transform = `translate(${state.panX}px, ${state.panY}px) scale(${state.scale})`;
        content.style.transformOrigin = "0 0";
        if (publish) {
            publishState(instance);
        }
    }

    function centerContent(instance) {
        const viewportWidth = Math.max(instance.viewport.clientWidth || 0, 1);
        const viewportHeight = Math.max(instance.viewport.clientHeight || 0, 1);
        const { x, y, width, height } = getContentMetrics(instance);
        const scaledWidth = width * instance.state.scale;
        const scaledHeight = height * instance.state.scale;

        instance.state.panX = (viewportWidth - scaledWidth) / 2 - x * instance.state.scale;
        instance.state.panY = (viewportHeight - scaledHeight) / 2 - y * instance.state.scale;
    }

    function physicalOwner(instance) {
        return instance.viewport.closest(".graph-canvas-panel-expanded") || instance.viewport.closest(".content");
    }

    function ownerBounds(owner) {
        const rect = owner.getBoundingClientRect();
        const style = getComputedStyle(owner);
        const visual = window.visualViewport;
        return {
            top: Math.max(rect.top + owner.clientTop, visual?.offsetTop || 0) + (parseFloat(style.paddingTop) || 0),
            bottom: Math.min(rect.top + owner.clientTop + owner.clientHeight, (visual?.offsetTop || 0) + (visual?.height || innerHeight)) - (parseFloat(style.paddingBottom) || 0)
        };
    }

    function sizePhysicalViewport(instance) {
        const frame = instance.viewport.closest(".graph-viewport-frame");
        const controls = frame?.querySelector(".graph-viewport-controls");
        const owner = physicalOwner(instance);
        if (!frame || !controls || !owner) {
            return;
        }
        frame.dataset.physicalHeight = "true";
        const bounds = ownerBounds(owner);
        const style = getComputedStyle(controls);
        const frameStyle = getComputedStyle(frame);
        const controlsHeight = controls.getBoundingClientRect().height + (parseFloat(style.marginTop) || 0) + (parseFloat(style.marginBottom) || 0);
        const frameInsets = ["paddingTop", "paddingBottom", "borderTopWidth", "borderBottomWidth"]
            .reduce((sum, key) => sum + (parseFloat(frameStyle[key]) || 0), 0);
        const height = Math.max(1, Math.floor(bounds.bottom - bounds.top - controlsHeight - frameInsets));
        const value = `${height}px`;
        if (frame.style.getPropertyValue("--graph-physical-height") !== value) {
            frame.style.setProperty("--graph-physical-height", value);
        }
    }

    function revealPhysicalViewport(instance) {
        const frame = instance.viewport.closest(".graph-viewport-frame");
        const owner = physicalOwner(instance);
        if (!frame || !owner) {
            return;
        }
        const bounds = ownerBounds(owner);
        const rect = frame.getBoundingClientRect();
        const delta = rect.top < bounds.top ? rect.top - bounds.top : rect.bottom > bounds.bottom ? rect.bottom - bounds.bottom : 0;
        if (Math.abs(delta) > 0.5) {
            owner.scrollTop += delta;
        }
    }

    function fitContent(viewport, reveal = false) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        sizePhysicalViewport(instance);
        arrangeLabels(instance);
        const nextScale = fitScale(instance, getContentMetrics(instance));

        instance.state.scale = Number.isFinite(nextScale) ? nextScale : 1;
        instance.state.hasInteracted = false;
        centerContent(instance);
        applyTransform(instance);
        if (reveal) {
            revealPhysicalViewport(instance);
        }
    }

    function resetContent(viewport) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        arrangeLabels(instance);
        instance.state.scale = 1;
        instance.state.hasInteracted = false;
        centerContent(instance);
        applyTransform(instance);
    }

    function zoomAt(viewport, clientX, clientY, multiplier) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        const rect = viewport.getBoundingClientRect();
        const localX = clientX - rect.left;
        const localY = clientY - rect.top;
        const previousScale = instance.state.scale;
        const lowerBound = Math.min(minScale, previousScale, fitScale(instance, getContentMetrics(instance)) / 4);
        const nextScale = clamp(previousScale * multiplier, lowerBound, maxScale);
        if (Math.abs(nextScale - previousScale) < Number.EPSILON) {
            return;
        }

        const worldX = (localX - instance.state.panX) / previousScale;
        const worldY = (localY - instance.state.panY) / previousScale;
        instance.state.scale = nextScale;
        instance.state.panX = localX - (worldX * nextScale);
        instance.state.panY = localY - (worldY * nextScale);
        instance.state.hasInteracted = true;
        applyTransform(instance);
    }

    function updatePanningState(instance, isPanning) {
        instance.state.isPanning = isPanning;
        instance.viewport.dataset.isPanning = isPanning ? "true" : "false";
    }

    function register(viewport, content) {
        if (!viewport || !content) {
            return;
        }

        const existing = getInstance(viewport);
        if (existing && existing.content === content) {
            applyTransform(existing);
            return;
        }
        if (existing) {
            unregister(viewport);
        }

        const instance = {
            viewport,
            content,
            state: {
                scale: 1,
                panX: padding,
                panY: padding,
                hasInteracted: false,
                isPanning: false,
                pointerId: null,
                startClientX: 0,
                startClientY: 0,
                startPanX: 0,
                startPanY: 0,
                startedOnNode: false,
                hasMoved: false,
                suppressNextClick: false
            },
            cleanup: [],
            frame: null,
            disposed: false,
            labelTransforms: new WeakMap(),
            contentObserver: null
        };

        const handleWheel = (event) => {
            event.preventDefault();
            const multiplier = event.deltaY < 0 ? 1.12 : 0.89;
            zoomAt(viewport, event.clientX, event.clientY, multiplier);
        };

        const handlePointerDown = (event) => {
            if (event.button !== 0) {
                return;
            }

            const controlTarget = event.target instanceof Element
                ? event.target.closest("button, input, select, textarea")
                : null;
            if (controlTarget) {
                return;
            }

            const nodeTarget = event.target instanceof Element
                ? event.target.closest(".graph-view-node")
                : null;
            instance.state.pointerId = event.pointerId;
            instance.state.startClientX = event.clientX;
            instance.state.startClientY = event.clientY;
            instance.state.startPanX = instance.state.panX;
            instance.state.startPanY = instance.state.panY;
            instance.state.startedOnNode = Boolean(nodeTarget);
            instance.state.hasMoved = false;
        };

        const handlePointerMove = (event) => {
            if (instance.state.pointerId !== event.pointerId) {
                return;
            }

            const deltaX = event.clientX - instance.state.startClientX;
            const deltaY = event.clientY - instance.state.startClientY;
            if (!instance.state.hasMoved && Math.hypot(deltaX, deltaY) >= dragThreshold) {
                instance.state.hasMoved = true;
                updatePanningState(instance, true);
                viewport.setPointerCapture?.(event.pointerId);
            }

            if (!instance.state.hasMoved) {
                return;
            }

            instance.state.panX = instance.state.startPanX + deltaX;
            instance.state.panY = instance.state.startPanY + deltaY;
            instance.state.hasInteracted = true;
            applyTransform(instance);
        };

        const handlePointerUp = (event) => {
            if (instance.state.pointerId !== event.pointerId) {
                return;
            }

            if (instance.state.isPanning) {
                viewport.releasePointerCapture?.(event.pointerId);
            }

            if (instance.state.startedOnNode && instance.state.hasMoved) {
                instance.state.suppressNextClick = true;
            }

            instance.state.pointerId = null;
            instance.state.startedOnNode = false;
            instance.state.hasMoved = false;
            updatePanningState(instance, false);
        };

        const handleClick = (event) => {
            if (!instance.state.suppressNextClick) {
                return;
            }

            instance.state.suppressNextClick = false;
            event.preventDefault();
            event.stopPropagation();
        };

        const handleDoubleClick = (event) => {
            const interactiveTarget = event.target instanceof Element
                ? event.target.closest(".graph-view-node")
                : null;
            if (interactiveTarget) {
                return;
            }

            fitContent(viewport);
        };

        const scheduleRefresh = () => {
            if (instance.disposed || instance.frame !== null) {
                return;
            }
            instance.frame = requestAnimationFrame(() => {
                instance.frame = null;
                if (!instance.disposed) {
                    refresh(viewport);
                }
            });
        };
        const resizeObserver = new ResizeObserver(scheduleRefresh);
        const physicalFrame = viewport.closest(".graph-viewport-frame");
        const previousPhysicalHeight = physicalFrame?.style.getPropertyValue("--graph-physical-height");
        const previousPhysicalAttribute = physicalFrame?.getAttribute("data-physical-height");
        // Do not observe the pan element's style: applyTransform would trigger a feedback loop.
        const contentObserver = new MutationObserver(records => {
            if (records.some(record => !(record.target === instance.content && record.type === "attributes" && record.attributeName === "style"))) {
                scheduleRefresh();
            }
        });
        instance.contentObserver = contentObserver;
        contentObserver.observe(content, { subtree: true, childList: true, characterData: true, attributes: true });
        const fontObserver = new MutationObserver(scheduleRefresh);
        fontObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["style", "class"] });
        document.fonts?.addEventListener("loadingdone", scheduleRefresh);
        document.fonts?.ready.then(scheduleRefresh);

        viewport.addEventListener("wheel", handleWheel, { passive: false });
        viewport.addEventListener("pointerdown", handlePointerDown);
        window.addEventListener("pointermove", handlePointerMove, true);
        window.addEventListener("pointerup", handlePointerUp, true);
        window.addEventListener("pointercancel", handlePointerUp, true);
        viewport.addEventListener("click", handleClick, true);
        viewport.addEventListener("dblclick", handleDoubleClick);
        resizeObserver.observe(viewport);
        for (const element of [viewport.closest(".content"), viewport.closest(".graph-canvas-panel"), physicalFrame?.querySelector(".graph-viewport-controls")]) {
            if (element) {
                resizeObserver.observe(element);
            }
        }
        window.visualViewport?.addEventListener("resize", scheduleRefresh);

        instance.cleanup.push(() => viewport.removeEventListener("wheel", handleWheel));
        instance.cleanup.push(() => viewport.removeEventListener("pointerdown", handlePointerDown));
        instance.cleanup.push(() => window.removeEventListener("pointermove", handlePointerMove, true));
        instance.cleanup.push(() => window.removeEventListener("pointerup", handlePointerUp, true));
        instance.cleanup.push(() => window.removeEventListener("pointercancel", handlePointerUp, true));
        instance.cleanup.push(() => viewport.removeEventListener("click", handleClick, true));
        instance.cleanup.push(() => viewport.removeEventListener("dblclick", handleDoubleClick));
        instance.cleanup.push(() => resizeObserver.disconnect());
        instance.cleanup.push(() => window.visualViewport?.removeEventListener("resize", scheduleRefresh));
        instance.cleanup.push(() => {
            if (physicalFrame) {
                if (previousPhysicalHeight) {
                    physicalFrame.style.setProperty("--graph-physical-height", previousPhysicalHeight);
                }
                else {
                    physicalFrame.style.removeProperty("--graph-physical-height");
                }
                if (previousPhysicalAttribute === null) {
                    physicalFrame.removeAttribute("data-physical-height");
                }
                else {
                    physicalFrame.setAttribute("data-physical-height", previousPhysicalAttribute);
                }
            }
        });
        instance.cleanup.push(() => contentObserver.disconnect());
        instance.cleanup.push(() => fontObserver.disconnect());
        instance.cleanup.push(() => document.fonts?.removeEventListener("loadingdone", scheduleRefresh));

        instances.set(viewport, instance);
        resetContent(viewport);
    }

    function unregister(viewport) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        instance.disposed = true;
        if (instance.frame !== null) {
            cancelAnimationFrame(instance.frame);
        }
        for (const dispose of instance.cleanup) {
            dispose();
        }

        for (const leader of instance.content.querySelectorAll(".graph-label-leader")) {
            leader.remove();
        }
        for (const label of instance.content.querySelectorAll(".graph-node-title")) {
            const original = instance.labelTransforms.get(label);
            if (original === null) {
                label.removeAttribute("transform");
            }
            else if (original !== undefined) {
                label.setAttribute("transform", original);
            }
        }

        instances.delete(viewport);
    }

    function refresh(viewport) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        sizePhysicalViewport(instance);
        if (instance.state.hasInteracted) {
            arrangeLabels(instance);
            applyTransform(instance);
        }
        else {
            fitContent(viewport);
        }
    }

    function zoomIn(viewport) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        const rect = viewport.getBoundingClientRect();
        zoomAt(viewport, rect.left + (rect.width / 2), rect.top + (rect.height / 2), 1.16);
    }

    function zoomOut(viewport) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        const rect = viewport.getBoundingClientRect();
        zoomAt(viewport, rect.left + (rect.width / 2), rect.top + (rect.height / 2), 0.86);
    }

    function panBy(viewport, dx, dy) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        instance.state.panX += dx;
        instance.state.panY += dy;
        instance.state.hasInteracted = true;
        applyTransform(instance);
    }

    function panUp(viewport) {
        panBy(viewport, 0, 80);
    }

    function panDown(viewport) {
        panBy(viewport, 0, -80);
    }

    function panLeft(viewport) {
        panBy(viewport, 80, 0);
    }

    function panRight(viewport) {
        panBy(viewport, -80, 0);
    }

    window.contextHubGraph = {
        register,
        unregister,
        refresh,
        fit: fitContent,
        reset: resetContent,
        zoomIn,
        zoomOut,
        panUp,
        panDown,
        panLeft,
        panRight
    };
})();
