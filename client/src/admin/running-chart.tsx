import { useEffect, useMemo, useRef, useState } from "react";
import type { RunningPoint } from "@/lib/workstream/api";

/*
 * How many desktops were running, as a stepped line over time: one series, so no legend; the card's title
 * names it. Hovering, or the arrow keys once it has focus, reads one point; the same numbers are a table for
 * a screen reader. Colors are the palette's classes, so it follows light and dark.
 */

const HEIGHT = 200;
const MARGIN = { top: 12, right: 14, bottom: 24, left: 30 };

const hour = (at: Date) => at.toLocaleTimeString([], { hour: "numeric" });
const moment = (at: Date) => at.toLocaleString([], { weekday: "short", hour: "numeric", minute: "2-digit" });
const desktops = (count: number) => `${count} ${count === 1 ? "desktop" : "desktops"} running`;

/** Whole-number ticks from zero to a top the largest value fits under, four steps at most. */
export function ticksFor(largest: number): number[] {
  const step = Math.max(1, Math.ceil(largest / 4));
  const top = Math.max(step, Math.ceil(largest / step) * step);
  const ticks: number[] = [];
  for (let value = 0; value <= top; value += step) ticks.push(value);
  return ticks;
}

/** The times between two moments where the clock reads midnight, six, noon or six. */
export function hourMarks(from: number, to: number): Date[] {
  const marks: Date[] = [];
  const start = new Date(from);
  start.setMinutes(0, 0, 0);
  for (let at = start.getTime(); at <= to; at += 3_600_000) {
    const mark = new Date(at);
    if (at >= from && mark.getHours() % 6 === 0) marks.push(mark);
  }
  return marks;
}

export function RunningChart({ points }: { points: RunningPoint[] }) {
  const frame = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);
  const [at, setAt] = useState<number | null>(null);

  useEffect(() => {
    const element = frame.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => setWidth(Math.floor(entry.contentRect.width)));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const series = useMemo(() => points.map((point) => ({ at: new Date(point.at), running: point.running })), [points]);
  const first = series[0]?.at.getTime() ?? 0;
  const last = series[series.length - 1]?.at.getTime() ?? 1;
  const ticks = ticksFor(Math.max(0, ...series.map((point) => point.running)));
  const top = ticks[ticks.length - 1];
  const plotWidth = Math.max(0, width - MARGIN.left - MARGIN.right);
  const plotHeight = HEIGHT - MARGIN.top - MARGIN.bottom;
  const x = (time: number) => MARGIN.left + (last === first ? 0 : ((time - first) / (last - first)) * plotWidth);
  const y = (value: number) => MARGIN.top + plotHeight - (value / top) * plotHeight;

  // A count holds until the next reading, so the line steps: across, then up or down.
  const line = series.map((point, index) => (index === 0 ? `M${x(point.at.getTime())},${y(point.running)}` : `H${x(point.at.getTime())}V${y(point.running)}`)).join("");
  const area = series.length > 0 ? `${line}V${y(0)}H${x(first)}Z` : "";

  // A label at each six-hour mark of the clock, so the day reads at a glance. The readings fall wherever the
  // half hours from now land, so the marks are found on the clock and not among the readings.
  const hours = useMemo(() => hourMarks(first, last), [first, last]);
  const shown = at === null ? null : series[at];

  const nearest = (clientX: number) => {
    const box = frame.current?.getBoundingClientRect();
    if (!box || series.length === 0 || plotWidth === 0) return null;
    const share = Math.min(1, Math.max(0, (clientX - box.left - MARGIN.left) / plotWidth));
    return Math.round(share * (series.length - 1));
  };

  return (
    <div
      ref={frame}
      className="relative rounded-md outline-none focus-visible:ring-2 focus-visible:ring-ring"
      style={{ height: HEIGHT }}
      tabIndex={0}
      role="group"
      aria-label="Desktops running over the last 24 hours. Use the left and right arrow keys to read a time."
      onPointerMove={(event) => setAt(nearest(event.clientX))}
      onPointerLeave={() => setAt(null)}
      onBlur={() => setAt(null)}
      onKeyDown={(event) => {
        if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
        event.preventDefault();
        const from = at ?? series.length - 1;
        setAt(Math.min(series.length - 1, Math.max(0, from + (event.key === "ArrowLeft" ? -1 : 1))));
      }}
    >
      {width > 0 && series.length > 1 ? (
        <svg width={width} height={HEIGHT} aria-hidden="true" className="block">
          {ticks.map((tick) => (
            <g key={tick}>
              <line x1={MARGIN.left} x2={width - MARGIN.right} y1={y(tick)} y2={y(tick)} className={tick === 0 ? "stroke-input" : "stroke-border"} strokeWidth={1} />
              <text x={MARGIN.left - 8} y={y(tick)} dy="0.32em" textAnchor="end" className="fill-muted-foreground text-[11px]">
                {tick}
              </text>
            </g>
          ))}
          {hours.map((mark) => (
            <text key={mark.getTime()} x={x(mark.getTime())} y={HEIGHT - 6} textAnchor="middle" className="fill-muted-foreground text-[11px]">
              {hour(mark)}
            </text>
          ))}
          <path d={area} className="fill-primary" opacity={0.1} />
          <path d={line} fill="none" className="stroke-primary" strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
          {shown ? (
            <g>
              <line x1={x(shown.at.getTime())} x2={x(shown.at.getTime())} y1={MARGIN.top} y2={y(0)} className="stroke-muted-foreground" strokeWidth={1} strokeDasharray="3 3" />
              <circle cx={x(shown.at.getTime())} cy={y(shown.running)} r={5} className="fill-primary stroke-card" strokeWidth={2} />
            </g>
          ) : null}
        </svg>
      ) : null}
      {shown && width > 0 ? (
        <div
          role="status"
          className="pointer-events-none absolute top-1 z-10 whitespace-nowrap rounded-md border border-border bg-card px-2.5 py-1.5 text-[12px] shadow-md"
          style={x(shown.at.getTime()) > width / 2 ? { right: width - x(shown.at.getTime()) + 10 } : { left: x(shown.at.getTime()) + 10 }}
        >
          <div className="font-semibold text-foreground">{desktops(shown.running)}</div>
          <div className="text-muted-foreground">{moment(shown.at)}</div>
        </div>
      ) : null}
      <table className="sr-only">
        <caption>Desktops running, every half hour over the last 24 hours</caption>
        <thead>
          <tr>
            <th scope="col">Time</th>
            <th scope="col">Running</th>
          </tr>
        </thead>
        <tbody>
          {series.map((point) => (
            <tr key={point.at.getTime()}>
              <td>{moment(point.at)}</td>
              <td>{point.running}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
