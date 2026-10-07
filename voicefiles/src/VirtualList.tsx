import { useEffect, useRef, useState, type ReactNode } from "react";

interface Props<T> {
  items: T[];
  rowHeight: number;
  renderRow: (item: T, index: number) => ReactNode;
  /** Scroll resets to the top when this changes. */
  scrollKey: string;
}

/**
 * Fixed-row-height windowed list: renders only the visible slice plus a
 * small overscan, so a directory (or result set) of any size stays cheap.
 */
export function VirtualList<T>({ items, rowHeight, renderRow, scrollKey }: Props<T>) {
  const outerRef = useRef<HTMLDivElement>(null);
  const [scrollTop, setScrollTop] = useState(0);
  const [viewHeight, setViewHeight] = useState(0);

  useEffect(() => {
    const el = outerRef.current!;
    const ro = new ResizeObserver(() => setViewHeight(el.clientHeight));
    ro.observe(el);
    setViewHeight(el.clientHeight);
    return () => ro.disconnect();
  }, []);

  useEffect(() => {
    outerRef.current?.scrollTo({ top: 0 });
    setScrollTop(0);
  }, [scrollKey]);

  const OVERSCAN = 8;
  const start = Math.max(0, Math.floor(scrollTop / rowHeight) - OVERSCAN);
  const end = Math.min(items.length, Math.ceil((scrollTop + viewHeight) / rowHeight) + OVERSCAN);

  return (
    <div
      ref={outerRef}
      onScroll={(e) => setScrollTop(e.currentTarget.scrollTop)}
      className="h-full overflow-y-auto"
    >
      <div style={{ height: items.length * rowHeight, position: "relative" }}>
        {items.slice(start, end).map((item, i) => (
          <div
            key={start + i}
            style={{
              position: "absolute",
              top: (start + i) * rowHeight,
              height: rowHeight,
              left: 0,
              right: 0,
            }}
          >
            {renderRow(item, start + i)}
          </div>
        ))}
      </div>
    </div>
  );
}
