<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import type { DashboardSample } from '../../types'

const props = defineProps<{
  samples: DashboardSample[]
  initialBalance: number | null
  /** 百分比模式：以初始余额为基准换算涨跌幅 */
  percentMode: boolean
}>()

const HEIGHT = 280
const PAD_LEFT = 56
const PAD_RIGHT = 12
const PAD_TOP = 12
const PAD_BOTTOM = 26
const FALLBACK_WIDTH = 720

interface Point {
  at: string
  value: number
}

const hoverIndex = ref<number | null>(null)
const hoverX = ref(0)

// 视口宽度决定 viewBox 宽度：固定宽度会被等比缩放居中，导致折线只占中间一段
const chartEl = ref<HTMLElement | null>(null)
const chartWidth = ref(FALLBACK_WIDTH)
let resizeObserver: ResizeObserver | null = null

onMounted(() => {
  if (!chartEl.value) return
  resizeObserver = new ResizeObserver((entries) => {
    const width = Math.round(entries[0]?.contentRect.width ?? 0)
    if (width > 0) chartWidth.value = Math.max(320, width)
  })
  resizeObserver.observe(chartEl.value)
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  resizeObserver = null
})

const points = computed<Point[]>(() => {
  const initial = props.initialBalance
  return props.samples.map((s) => ({
    at: s.at,
    value:
      props.percentMode && initial && initial > 0
        ? ((s.equity - initial) / initial) * 100
        : s.equity,
  }))
})

const hasData = computed(() => points.value.length > 0)

const scale = computed(() => {
  const values = points.value.map((p) => p.value)
  const reference = referenceValue.value
  if (reference !== null) values.push(reference)
  let min = Math.min(...values)
  let max = Math.max(...values)
  if (min === max) {
    const pad = Math.abs(min) * 0.01 || 1
    min -= pad
    max += pad
  }
  const pad = (max - min) * 0.08
  min -= pad
  max += pad
  // 采样几乎不变时（如实盘空仓、净值恒定），把定义域撑到最小跨度，
  // 否则 4 个刻度会因整数格式化全部显示成同一个值。
  const minSpan = Math.max(Math.abs((min + max) / 2) * 0.004, props.percentMode ? 0.2 : 0.04)
  if (max - min < minSpan) {
    const center = (min + max) / 2
    min = center - minSpan / 2
    max = center + minSpan / 2
  }
  return { min, max }
})

const referenceValue = computed(() => {
  if (props.percentMode) return 0
  return props.initialBalance
})

function x(index: number): number {
  const count = points.value.length
  if (count <= 1) return PAD_LEFT + (chartWidth.value - PAD_LEFT - PAD_RIGHT) / 2
  return PAD_LEFT + (index / (count - 1)) * (chartWidth.value - PAD_LEFT - PAD_RIGHT)
}

function y(value: number): number {
  const { min, max } = scale.value
  const ratio = (value - min) / (max - min || 1)
  return PAD_TOP + (1 - ratio) * (HEIGHT - PAD_TOP - PAD_BOTTOM)
}

const linePath = computed(() => {
  const pts = points.value
  if (pts.length === 0) return ''
  if (pts.length === 1) {
    const py = y(pts[0].value)
    return `M ${x(0) - 24} ${py} L ${x(0) + 24} ${py}`
  }
  // 轻度平滑：控制点取相邻点 1/6 偏移
  const coords = pts.map((p, i) => [x(i), y(p.value)] as [number, number])
  let d = `M ${coords[0][0]} ${coords[0][1]}`
  for (let i = 0; i < coords.length - 1; i++) {
    const [x0, y0] = coords[i]
    const [x1, y1] = coords[i + 1]
    const dx = (x1 - x0) / 3
    d += ` C ${x0 + dx} ${y0}, ${x1 - dx} ${y1}, ${x1} ${y1}`
  }
  return d
})

const yTicks = computed(() => {
  const { min, max } = scale.value
  const ticks: { value: number; y: number; label: string }[] = []
  for (let i = 0; i <= 4; i++) {
    const value = min + ((max - min) * i) / 4
    ticks.push({ value, y: y(value), label: formatAxis(value) })
  }
  return ticks
})

const xTicks = computed(() => {
  const count = points.value.length
  if (count === 0) return []
  const tickCount = Math.min(6, count)
  const indexes = new Set<number>()
  for (let i = 0; i < tickCount; i++) {
    indexes.add(Math.round((i / Math.max(1, tickCount - 1)) * (count - 1)))
  }
  return [...indexes].sort((a, b) => a - b).map((i) => ({ x: x(i), label: formatTime(points.value[i].at) }))
})

function formatAxis(value: number): string {
  if (props.percentMode) return `${value.toFixed(1)}%`
  if (value >= 10000) return `$${(value / 1000).toFixed(1)}k`
  // 按当前定义域跨度选小数位：跨度小的时候必须保留小数，否则刻度会重复
  const span = scale.value.max - scale.value.min
  const decimals = span < 1 ? 2 : span < 20 ? 1 : 0
  return `$${value.toFixed(decimals)}`
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

function onMove(event: MouseEvent) {
  const target = event.currentTarget as SVGElement
  const rect = target.getBoundingClientRect()
  const scaleX = chartWidth.value / rect.width
  const px = (event.clientX - rect.left) * scaleX
  const count = points.value.length
  if (count === 0) return
  let best = 0
  let bestDist = Number.POSITIVE_INFINITY
  for (let i = 0; i < count; i++) {
    const dist = Math.abs(x(i) - px)
    if (dist < bestDist) {
      bestDist = dist
      best = i
    }
  }
  hoverIndex.value = best
  hoverX.value = (x(best) / chartWidth.value) * rect.width
}

function onLeave() {
  hoverIndex.value = null
}

const hoverPoint = computed(() => {
  if (hoverIndex.value === null) return null
  const p = points.value[hoverIndex.value]
  const initial = props.initialBalance
  const delta = initial !== null && initial > 0 ? ((p.value - (props.percentMode ? initial : initial)) / initial) * 100 : null
  return { ...p, delta }
})
</script>

<template>
  <div ref="chartEl" class="equity-chart">
    <div v-if="!hasData" class="chart-empty">
      <div class="empty-icon">$</div>
      <div class="empty-text">暂无历史数据</div>
      <div class="empty-sub">保持服务运行即可——每约 1 分钟采样一次账户净值（钱包余额 + 未实现盈亏）</div>
    </div>
    <template v-else>
      <svg
        :viewBox="`0 0 ${chartWidth} ${HEIGHT}`"
        class="chart-svg"
        @mousemove="onMove"
        @mouseleave="onLeave"
      >
        <defs>
          <linearGradient id="goldStroke" gradientUnits="userSpaceOnUse" x1="0" :y1="PAD_TOP" x2="0" :y2="HEIGHT - PAD_BOTTOM">
            <stop offset="0%" stop-color="#F0B90B" stop-opacity="0.95" />
            <stop offset="100%" stop-color="#FCD535" stop-opacity="0.35" />
          </linearGradient>
        </defs>

        <!-- 水印 -->
        <text :x="chartWidth - PAD_RIGHT" :y="26" text-anchor="end" class="watermark">招财猫</text>

        <!-- 网格与 Y 轴 -->
        <g v-for="tick in yTicks" :key="`y-${tick.value}`">
          <line
            :x1="PAD_LEFT"
            :x2="chartWidth - PAD_RIGHT"
            :y1="tick.y"
            :y2="tick.y"
            class="grid-line"
          />
          <text :x="PAD_LEFT - 8" :y="tick.y + 3" text-anchor="end" class="axis-text">{{ tick.label }}</text>
        </g>

        <!-- 初始余额参考线 -->
        <template v-if="referenceValue !== null && referenceValue >= scale.min && referenceValue <= scale.max">
          <line
            :x1="PAD_LEFT"
            :x2="chartWidth - PAD_RIGHT"
            :y1="y(referenceValue)"
            :y2="y(referenceValue)"
            class="reference-line"
          />
          <text :x="chartWidth - PAD_RIGHT - 4" :y="y(referenceValue) - 5" text-anchor="end" class="reference-text">
            {{ percentMode ? '基准 0%' : `初始余额 ${initialBalance}` }}
          </text>
        </template>

        <!-- 曲线 -->
        <path :d="linePath" class="equity-line" />

        <!-- 数据点（≤50 显示） -->
        <template v-if="points.length <= 50">
          <circle v-for="(p, i) in points" :key="i" :cx="x(i)" :cy="y(p.value)" r="2.5" class="data-dot" />
        </template>

        <!-- 悬停 -->
        <template v-if="hoverIndex !== null">
          <line :x1="x(hoverIndex)" :x2="x(hoverIndex)" :y1="PAD_TOP" :y2="HEIGHT - PAD_BOTTOM" class="hover-line" />
          <circle :cx="x(hoverIndex)" :cy="y(points[hoverIndex].value)" r="5" class="hover-dot" />
        </template>

        <!-- X 轴 -->
        <text
          v-for="tick in xTicks"
          :key="`x-${tick.x}`"
          :x="tick.x"
          :y="HEIGHT - 6"
          text-anchor="middle"
          class="axis-text"
        >
          {{ tick.label }}
        </text>
      </svg>

      <div
        v-if="hoverPoint"
        class="hover-tooltip"
        :style="{ left: `${Math.min(Math.max(hoverX, 60), 560)}px` }"
      >
        <div class="tooltip-time">{{ new Date(hoverPoint.at).toLocaleString() }}</div>
        <div class="tooltip-value">{{ percentMode ? `${hoverPoint.value.toFixed(2)}%` : `$${hoverPoint.value.toFixed(2)}` }}</div>
        <div v-if="hoverPoint.delta !== null" class="tooltip-delta" :class="hoverPoint.delta >= 0 ? 'gain' : 'loss'">
          {{ hoverPoint.delta >= 0 ? '+' : '' }}{{ hoverPoint.delta.toFixed(2) }}%
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.equity-chart {
  position: relative;
  width: 100%;
  display: flex;
  flex-direction: column;
}

.chart-svg {
  width: 100%;
  height: 280px;
  display: block;
  flex-shrink: 0;
}

.watermark {
  font-size: 14px;
  font-weight: 700;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  fill: rgba(240, 185, 11, 0.18);
  letter-spacing: 1px;
}

.grid-line {
  stroke: var(--color-border-1);
  stroke-dasharray: 3 3;
}

.axis-text {
  font-size: 10px;
  fill: var(--color-text-3);
}

.reference-line {
  stroke: var(--color-text-4);
  stroke-dasharray: 5 4;
}

.reference-text {
  font-size: 10px;
  fill: var(--color-text-3);
}

.equity-line {
  fill: none;
  stroke: url(#goldStroke);
  stroke-width: 3;
  stroke-linecap: round;
}

.data-dot {
  fill: rgb(var(--primary-6));
}

.hover-line {
  stroke: var(--color-text-4);
  stroke-dasharray: 3 3;
}

.hover-dot {
  fill: #fcd535;
  stroke: rgb(var(--primary-6));
  stroke-width: 2;
}

.hover-tooltip {
  position: absolute;
  top: 12px;
  transform: translateX(-50%);
  background: var(--color-bg-2);
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  padding: 8px 12px;
  pointer-events: none;
  display: flex;
  flex-direction: column;
  gap: 2px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.08);
  z-index: 2;
}

.tooltip-time {
  font-size: 11px;
  color: var(--color-text-3);
}

.tooltip-value {
  font-size: 14px;
  font-weight: 700;
  color: var(--color-text-1);
}

.tooltip-delta {
  font-size: 12px;
  font-weight: 600;
}

.gain {
  color: rgb(var(--success-6));
}

.loss {
  color: rgb(var(--danger-6));
}

.chart-empty {
  flex: 1;
  min-height: 240px;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
}

.empty-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 44px;
  height: 44px;
  border-radius: 50%;
  background: var(--color-fill-2);
  color: var(--color-text-3);
  font-size: 20px;
  font-weight: 700;
}

.empty-text {
  font-size: 14px;
  color: var(--color-text-3);
}

.empty-sub {
  font-size: 12px;
  color: var(--color-text-4);
}
</style>
