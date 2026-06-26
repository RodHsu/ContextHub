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

    function getContentMetrics(instance) {
        return {
            width: Math.max(instance.content.offsetWidth || 0, 1),
            height: Math.max(instance.content.offsetHeight || 0, 1)
        };
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
        const { width, height } = getContentMetrics(instance);
        const scaledWidth = width * instance.state.scale;
        const scaledHeight = height * instance.state.scale;

        instance.state.panX = scaledWidth < viewportWidth - (padding * 2)
            ? (viewportWidth - scaledWidth) / 2
            : padding;
        instance.state.panY = scaledHeight < viewportHeight - (padding * 2)
            ? (viewportHeight - scaledHeight) / 2
            : padding;
    }

    function fitContent(viewport) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        const viewportWidth = Math.max(viewport.clientWidth || 0, 1);
        const viewportHeight = Math.max(viewport.clientHeight || 0, 1);
        const { width, height } = getContentMetrics(instance);
        const nextScale = clamp(
            Math.min((viewportWidth - (padding * 2)) / width, (viewportHeight - (padding * 2)) / height),
            minScale,
            maxScale);

        instance.state.scale = Number.isFinite(nextScale) ? nextScale : 1;
        instance.state.hasInteracted = false;
        centerContent(instance);
        applyTransform(instance);
    }

    function resetContent(viewport) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

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
        const nextScale = clamp(previousScale * multiplier, minScale, maxScale);
        if (Math.abs(nextScale - previousScale) < 0.0001) {
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
        if (existing) {
            existing.content = content;
            applyTransform(existing);
            return;
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
            cleanup: []
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

        const resizeObserver = new ResizeObserver(() => {
            if (instance.state.hasInteracted) {
                applyTransform(instance);
            }
            else {
                fitContent(viewport);
            }
        });

        viewport.addEventListener("wheel", handleWheel, { passive: false });
        viewport.addEventListener("pointerdown", handlePointerDown);
        window.addEventListener("pointermove", handlePointerMove, true);
        window.addEventListener("pointerup", handlePointerUp, true);
        window.addEventListener("pointercancel", handlePointerUp, true);
        viewport.addEventListener("click", handleClick, true);
        viewport.addEventListener("dblclick", handleDoubleClick);
        resizeObserver.observe(viewport);

        instance.cleanup.push(() => viewport.removeEventListener("wheel", handleWheel));
        instance.cleanup.push(() => viewport.removeEventListener("pointerdown", handlePointerDown));
        instance.cleanup.push(() => window.removeEventListener("pointermove", handlePointerMove, true));
        instance.cleanup.push(() => window.removeEventListener("pointerup", handlePointerUp, true));
        instance.cleanup.push(() => window.removeEventListener("pointercancel", handlePointerUp, true));
        instance.cleanup.push(() => viewport.removeEventListener("click", handleClick, true));
        instance.cleanup.push(() => viewport.removeEventListener("dblclick", handleDoubleClick));
        instance.cleanup.push(() => resizeObserver.disconnect());

        instances.set(viewport, instance);
        resetContent(viewport);
    }

    function unregister(viewport) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        for (const dispose of instance.cleanup) {
            dispose();
        }

        instances.delete(viewport);
    }

    function refresh(viewport) {
        const instance = getInstance(viewport);
        if (!instance) {
            return;
        }

        if (instance.state.hasInteracted) {
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
