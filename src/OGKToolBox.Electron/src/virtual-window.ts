import { useEffect, useMemo, useRef, useState } from "react";

type WindowState = {
  scrollTop: number;
  viewportHeight: number;
  viewportWidth: number;
};

function useViewport() {
  const ref = useRef<HTMLDivElement>(null);
  const [state, setState] = useState<WindowState>({ scrollTop: 0, viewportHeight: 0, viewportWidth: 0 });

  useEffect(() => {
    const element = ref.current;
    if (!element) return;

    const update = () => setState({
      scrollTop: element.scrollTop,
      viewportHeight: element.clientHeight,
      viewportWidth: element.clientWidth,
    });
    const resize = new ResizeObserver(update);
    resize.observe(element);
    element.addEventListener("scroll", update, { passive: true });
    update();
    return () => {
      resize.disconnect();
      element.removeEventListener("scroll", update);
    };
  }, []);

  return { ref, state };
}

function calculateWindow(itemCount: number, rowHeight: number, columns: number, state: WindowState, overscanRows: number) {
  const rowCount = Math.ceil(itemCount / columns);
  const visibleFirstRow = Math.min(Math.max(0, rowCount - 1), Math.max(0, Math.floor(state.scrollTop / rowHeight)));
  const visibleRows = Math.ceil(Math.max(state.viewportHeight, rowHeight) / rowHeight);
  const visibleLastRow = Math.min(rowCount, visibleFirstRow + visibleRows);
  const firstRow = Math.max(0, visibleFirstRow - overscanRows);
  const lastRow = Math.min(rowCount, visibleLastRow + overscanRows);
  return {
    startIndex: firstRow * columns,
    endIndex: Math.min(itemCount, lastRow * columns),
    visibleStartIndex: visibleFirstRow * columns,
    visibleEndIndex: Math.min(itemCount, visibleLastRow * columns),
    paddingTop: firstRow * rowHeight,
    paddingBottom: Math.max(0, (rowCount - lastRow) * rowHeight),
  };
}

export function useVirtualList(itemCount: number, rowHeight: number, overscanRows = 3, resetKey?: string) {
  const { ref, state } = useViewport();
  useEffect(() => {
    if (ref.current) ref.current.scrollTop = 0;
  }, [resetKey, ref]);
  useEffect(() => {
    if (ref.current && ref.current.scrollTop > itemCount * rowHeight) ref.current.scrollTop = 0;
  }, [itemCount, rowHeight, ref]);
  const window = useMemo(
    () => calculateWindow(itemCount, rowHeight, 1, state, overscanRows),
    [itemCount, rowHeight, state, overscanRows],
  );
  return { ref, ...window };
}

export function useVirtualCardGrid(
  itemCount: number,
  minColumnWidth = 190,
  gap = 12,
  overscanRows = 1,
  resetKey?: string,
) {
  const { ref, state } = useViewport();
  const columns = Math.max(1, Math.floor((state.viewportWidth + gap) / (minColumnWidth + gap)));
  const columnWidth = Math.max(minColumnWidth, (state.viewportWidth - gap * (columns - 1) - 7) / columns);
  // Keep this in sync with .card-art. This is tall enough to preserve the full
  // portrait thumbnail while still reserving room for two lines of metadata.
  const rowHeight = Math.max(302, (columnWidth - 18) * (4 / 3) + 88) + gap;
  useEffect(() => {
    if (ref.current) ref.current.scrollTop = 0;
  }, [resetKey, ref]);
  useEffect(() => {
    const rowCount = Math.ceil(itemCount / columns);
    if (ref.current && ref.current.scrollTop > rowCount * rowHeight) ref.current.scrollTop = 0;
  }, [itemCount, columns, rowHeight, ref]);
  const window = useMemo(
    // Keep a small render buffer so the grid never exposes an empty strip while scrolling.
    // Thumbnail requests are separately gated by the element's actual visibility.
    () => calculateWindow(itemCount, rowHeight, columns, state, overscanRows),
    [itemCount, rowHeight, columns, state, overscanRows],
  );
  return {
    ref,
    columns,
    rowHeight,
    ...window,
    paddingTop: window.paddingTop > 0 ? Math.max(0, window.paddingTop - gap) : 0,
    paddingBottom: window.paddingBottom > 0 ? Math.max(0, window.paddingBottom - gap) : 0,
  };
}
