<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Message } from '@arco-design/web-vue'
import { IconArrowFall, IconArrowRise, IconClockCircle, IconCopy, IconDashboard, IconDownload, IconRobot } from '@arco-design/web-vue/es/icon'
import { closeTraderPosition, getTraderDashboard, listTraders } from '../api'
import LuckyCatScene from '../components/trader/LuckyCatScene.vue'
import type { DashboardDecision, DashboardPosition, Trader, TraderDashboard, TraderOrder } from '../types'
import EquityChart from '../components/trader/EquityChart.vue'
import MarkdownBlock from '../components/trader/MarkdownBlock.vue'

const route = useRoute()
const router = useRouter()

const traders = ref<Trader[]>([])
const dashboard = ref<TraderDashboard | null>(null)
const loading = ref(false)
const errorText = ref<string | null>(null)
const percentMode = ref(false)
const decisionsLimit = ref(2)

// 走动的时钟：像「运行时间」这类随时间增长的显示，computed 的依赖不含 Date.now()，
// 事件驱动（无定时拉取）下不会自行重算——显式依赖这个每 30 秒更新的 ref（UI 时钟，非数据轮询）
const clock = ref(Date.now())
let clockTimer: number | undefined
const closingSymbol = ref<string | null>(null)

// 实时推送（SSE）：持仓/余额来自交易所 WS 订阅、行情 ticker 驱动未实现盈亏、最新决策/审计行/周期状态/
// 净值样本随事件秒推（事件总线），历史变化时触发一次聚合刷新——不再有定时轮询；
// 断线自动重连（EventSource 被终止时按 3 秒节拍重建），重连后重同步一次聚合数据。
const liveConnected = ref(false)
const realtimeMarks = ref<Record<string, number>>({})
const realtimePriceType = ref<string | null>(null)
let eventSource: EventSource | null = null
let streamOpenedOnce = false
let lastFrameAt = 0
let watchdogTimer: number | undefined

// 链路判活：任何一帧（含后端心跳 ping）都刷新；看门狗在 45 秒无帧时判定连接已死并重建
// （后端重启/代理静默断开时，EventSource 可能既不报错也不重连）
function markAlive() {
  lastFrameAt = Date.now()
  liveConnected.value = true
}

function startWatchdog(traderId: number) {
  if (watchdogTimer !== undefined) window.clearInterval(watchdogTimer)
  watchdogTimer = window.setInterval(() => {
    if (eventSource && Date.now() - lastFrameAt > 45000) {
      openStream(traderId)
    }
  }, 15000)
}

// 历史信号触发的聚合刷新节流（同步循环可能连续上报；3 秒内只刷一次，避免自激）
let lastHistoryRefreshAt = 0

// SSE 秒推的决策记录：可能与首次 HTTP 响应在途存在竞态（新决策先经 SSE 到达、HTTP 快照稍后覆盖），
// 故先缓存再合并——后台决策记录只增不改，按 id 去重合并即可（更早明细由种子帧与聚合刷新补齐）。
const sseDecisions = ref<DashboardDecision[]>([])

function mergeDecisions(current: DashboardDecision[], incoming: DashboardDecision[]): DashboardDecision[] {
  const byId = new Map<number, DashboardDecision>()
  for (const d of current) byId.set(d.id, d)
  for (const d of incoming) byId.set(d.id, d)
  return [...byId.values()].sort((a, b) => b.id - a.id).slice(0, 50)
}

function applySseDecisions() {
  if (dashboard.value && sseDecisions.value.length > 0) {
    dashboard.value.decisions = mergeDecisions(dashboard.value.decisions, sseDecisions.value)
  }
}

// 审计行插入（按 Id 去重，倒序保留 20 条）；拉取在途期间 SSE 已插入的行在合并时复用同一逻辑
function upsertOrder(row: TraderOrder) {
  if (!dashboard.value || typeof row?.id !== 'number') return
  const rest = dashboard.value.recentOrders.filter((o) => o.id !== row.id)
  dashboard.value.recentOrders = [row, ...rest].sort((a, b) => b.id - a.id).slice(0, 20)
}

// 净值样本点追加（时间升序，保留最近 200 个；不早于最后一个点时忽略）
function appendSample(at: string, equity: number) {
  if (!dashboard.value || !at || typeof equity !== 'number') return
  const samples = dashboard.value.samples
  if (samples.length > 0 && samples[samples.length - 1].at >= at) return
  dashboard.value.samples = [...samples, { at, equity }].slice(-200)
}

// 价格列名随实际来源如实显示：默认「最新价」（行情 WS 回退口径），来源为标记价时显示「标记价」
const priceHeader = computed(() => (realtimePriceType.value === 'mark' ? '标记价' : '最新价'))

function closeStream() {
  eventSource?.close()
  eventSource = null
  liveConnected.value = false
  realtimeMarks.value = {}
  realtimePriceType.value = null
  sseDecisions.value = []
  if (watchdogTimer !== undefined) {
    window.clearInterval(watchdogTimer)
    watchdogTimer = undefined
  }
}

function openStream(traderId: number) {
  closeStream()
  const source = new EventSource(`/api/traders/${traderId}/stream`)
  eventSource = source
  source.onopen = () => {
    markAlive()
    // 重连（首次连接由 onMounted 拉取）：断开期间的事件无法补发，重同步一次聚合数据
    if (streamOpenedOnce) void loadDashboard(true)
    streamOpenedOnce = true
  }
  source.onerror = () => {
    liveConnected.value = false
    // 后端不可达/代理返回非 200 时浏览器会「终止」EventSource（规范：不再自动重试）：按 3 秒节拍重建
    if (source.readyState === EventSource.CLOSED) {
      window.setTimeout(() => {
        if (eventSource === source) openStream(traderId)
      }, 3000)
    }
  }
  source.addEventListener('ping', () => markAlive())
  source.addEventListener('state', (event) => {
    markAlive()
    try {
      const payload = JSON.parse((event as MessageEvent).data) as {
        at: string
        equity: number | null
        available: number | null
        priceType: string | null
        marks: Record<string, number>
        positions: Array<{
          symbol: string
          side: string
          quantity: number
          averageOpenPrice: number
          unrealizedPnl: number | null
          liquidationPrice: number | null
          leverage: number | null
        }>
      }
      realtimeMarks.value = payload.marks ?? {}
      realtimePriceType.value = payload.priceType ?? null
      if (!dashboard.value) return
      dashboard.value.positions = (payload.positions ?? []).map((p) => ({
        symbol: p.symbol,
        side: p.side,
        quantity: p.quantity,
        averageOpenPrice: p.averageOpenPrice,
        unrealizedPnl: p.unrealizedPnl,
        liquidationPrice: p.liquidationPrice,
        leverage: p.leverage,
      }))
      if (dashboard.value.account) {
        if (typeof payload.equity === 'number') dashboard.value.account.equity = payload.equity
        if (typeof payload.available === 'number') dashboard.value.account.available = payload.available
        dashboard.value.account.at = payload.at
        dashboard.value.account.positionCount = dashboard.value.positions.length
      }
    } catch {
      // 忽略无效帧
    }
  })
  source.addEventListener('decisions', (event) => {
    markAlive()
    try {
      const payload = JSON.parse((event as MessageEvent).data) as { decisions?: DashboardDecision[] }
      sseDecisions.value = mergeDecisions(sseDecisions.value, payload.decisions ?? [])
      applySseDecisions()
    } catch {
      // 忽略无效帧
    }
  })
  // 执行审计：新行插入列表头（按 Id 去重，保留 20 条）
  source.addEventListener('order', (event) => {
    markAlive()
    try {
      upsertOrder(JSON.parse((event as MessageEvent).data) as TraderOrder)
    } catch {
      // 忽略无效帧
    }
  })
  // 周期状态：更新顶部状态行的最近周期时间/结果/运行状态
  source.addEventListener('cycle', (event) => {
    markAlive()
    try {
      const payload = JSON.parse((event as MessageEvent).data) as {
        at: string | null
        status: string | null
        isRunning: boolean
      }
      if (!dashboard.value) return
      dashboard.value.trader.lastCycleAt = payload.at
      dashboard.value.trader.lastCycleStatus = payload.status
      dashboard.value.trader.isRunning = payload.isRunning
    } catch {
      // 忽略无效帧
    }
  })
  // 净值样本：新点到图表（时间升序，保留最近 200 个）
  source.addEventListener('equity', (event) => {
    markAlive()
    try {
      const payload = JSON.parse((event as MessageEvent).data) as { at: string; equity: number }
      appendSample(payload?.at, payload?.equity)
    } catch {
      // 忽略无效帧
    }
  })
  // 仓位历史变化：聚合（历史/指标/品种表现）由后端算，触发一次刷新（节流）
  source.addEventListener('history', () => {
    markAlive()
    const now = Date.now()
    if (now - lastHistoryRefreshAt < 3000) return
    lastHistoryRefreshAt = now
    void loadDashboard(true)
  })

  startWatchdog(traderId)
}

// 单仓未实现盈亏：有实时标记价用标记价重算（毫秒级跟随），否则用接口/推送值
function livePnl(position: DashboardPosition): number | null {
  const mark = realtimeMarks.value[position.symbol]
  if (mark === undefined || position.averageOpenPrice <= 0) return position.unrealizedPnl
  const quantity = Math.abs(position.quantity)
  const isLong = position.side.toLowerCase().includes('long') || position.side === 'Buy'
  return (mark - position.averageOpenPrice) * quantity * (isLong ? 1 : -1)
}

// 净值口径：钱包余额 + 未实现盈亏（保证金余额语义），实时价可用时逐仓用实时价
const displayUnrealizedTotal = computed(() => {
  const positions = dashboard.value?.positions ?? []
  if (positions.length === 0) return 0
  let total = 0
  for (const p of positions) {
    total += livePnl(p) ?? 0
  }
  return total
})

const displayEquity = computed(() => {
  const account = dashboard.value?.account
  if (!account?.success || account.equity === null) return null
  return account.equity + displayUnrealizedTotal.value
})

const displayTotalPnl = computed(() => {
  const equity = displayEquity.value
  const initial = dashboard.value?.trader.initialBalance
  if (equity === null || initial == null || initial <= 0) return null
  return equity - initial
})

const displayPnlPct = computed(() => {
  const pnl = displayTotalPnl.value
  const initial = dashboard.value?.trader.initialBalance
  if (pnl === null || initial == null || initial <= 0) return null
  return (pnl / initial) * 100
})

// 仓位历史分页（默认每页 10 条，可选页容量）+ 筛选（标的/方向）
const HISTORY_PAGE_SIZE_OPTIONS = [10, 20, 50]
const SIDE_FILTER_OPTIONS = [
  { value: 'Long', label: '多头' },
  { value: 'Short', label: '空头' },
]
const historyPageSize = ref(HISTORY_PAGE_SIZE_OPTIONS[0])
const historyPage = ref(1)
const historyFilterSymbol = ref<string | undefined>(undefined)
const historyFilterSide = ref<string | undefined>(undefined)

const allHistoryRows = computed(() => dashboard.value?.history.rows ?? [])
const symbolOptions = computed(() =>
  [...new Set(allHistoryRows.value.map((r) => r.symbol))].sort((a, b) => a.localeCompare(b)),
)
const filteredHistoryRows = computed(() =>
  allHistoryRows.value.filter(
    (r) =>
      (!historyFilterSymbol.value || r.symbol === historyFilterSymbol.value) &&
      (!historyFilterSide.value || (r.side ?? '') === historyFilterSide.value),
  ),
)
const historyPageCount = computed(() => Math.max(1, Math.ceil(filteredHistoryRows.value.length / historyPageSize.value)))
const pagedHistoryRows = computed(() =>
  filteredHistoryRows.value.slice((historyPage.value - 1) * historyPageSize.value, historyPage.value * historyPageSize.value),
)

function onHistoryPageSizeChange(size: number) {
  historyPageSize.value = size
  historyPage.value = 1
}

watch([historyFilterSymbol, historyFilterSide], () => {
  historyPage.value = 1
})

// 品种表现（从当前行数据按标的筛选实时聚合——与后端口径一致）+ 同款分页
const symbolPageSize = ref(HISTORY_PAGE_SIZE_OPTIONS[0])
const symbolPage = ref(1)
const symbolFilterSymbol = ref<string | undefined>(undefined)

const filteredSymbolStats = computed(() => {
  const rows = allHistoryRows.value.filter((r) => !symbolFilterSymbol.value || r.symbol === symbolFilterSymbol.value)
  const groups = new Map<string, { count: number; wins: number; pnl: number }>()
  for (const row of rows) {
    const group = groups.get(row.symbol) ?? { count: 0, wins: 0, pnl: 0 }
    group.count++
    if (row.realizedPnl > 0) group.wins++
    group.pnl += row.realizedPnl
    groups.set(row.symbol, group)
  }
  return [...groups.entries()]
    .map(([symbol, g]) => ({
      symbol,
      count: g.count,
      winRate: g.count > 0 ? Math.round((g.wins / g.count) * 10000) / 100 : 0,
      totalPnl: Math.round(g.pnl * 10000) / 10000,
    }))
    .sort((a, b) => b.count - a.count || a.symbol.localeCompare(b.symbol))
})

const symbolPageCount = computed(() => Math.max(1, Math.ceil(filteredSymbolStats.value.length / symbolPageSize.value)))
const pagedSymbols = computed(() =>
  filteredSymbolStats.value.slice((symbolPage.value - 1) * symbolPageSize.value, symbolPage.value * symbolPageSize.value),
)

function onSymbolPageSizeChange(size: number) {
  symbolPageSize.value = size
  symbolPage.value = 1
}

watch(symbolFilterSymbol, () => {
  symbolPage.value = 1
})

// 方向统计：固定展示 多头/空头 两卡（无记录的方向显示 0 次 / —）；方向整体不可得时整行隐藏
interface DirectionCard {
  side: string
  label: string
  count: number
  winRate: number | null
  totalPnl: number
  avgPnl: number | null
  empty: boolean
}

const directionCards = computed<DirectionCard[]>(() => {
  const dirs = dashboard.value?.history.directions ?? []
  if (!dirs.some((d) => d.side)) return []
  return (['Long', 'Short'] as const).map((side) => {
    const found = dirs.find((d) => d.side === side)
    return {
      side,
      label: side === 'Long' ? '多头' : '空头',
      count: found?.count ?? 0,
      winRate: found ? found.winRate : null,
      totalPnl: found?.totalPnl ?? 0,
      avgPnl: found ? found.avgPnl : null,
      empty: !found || found.count === 0,
    }
  })
})

const selectedId = computed<number | null>(() => {
  const raw = route.query.trader
  const id = Number(Array.isArray(raw) ? raw[0] : raw)
  return Number.isFinite(id) && id > 0 ? id : null
})

async function loadTraders() {
  traders.value = await listTraders()
  if (selectedId.value === null && traders.value.length > 0) {
    await router.replace({ path: '/dashboard', query: { trader: String(traders.value[0].id) } })
  } else if (selectedId.value !== null && !traders.value.some((t) => t.id === selectedId.value) && traders.value.length > 0) {
    await router.replace({ path: '/dashboard', query: { trader: String(traders.value[0].id) } })
  }
}

async function loadDashboard(silent = false) {
  const id = selectedId.value
  if (id === null) return
  if (!silent) loading.value = true
  try {
    const previous = dashboard.value
    const fetched = await getTraderDashboard(id)
    dashboard.value = fetched
    // 拉取在途期间 SSE 已推送的更新（审计行/净值样本点/决策）并回，避免被旧快照覆盖；跨交易员不合并
    if (previous && previous.trader.id === fetched.trader.id) {
      for (const order of previous.recentOrders) upsertOrder(order)
      for (const sample of previous.samples) appendSample(sample.at, sample.equity)
    }
    applySseDecisions()
    errorText.value = null
  } catch (err) {
    if (!silent) errorText.value = err instanceof Error ? err.message : '加载失败'
  } finally {
    if (!silent) loading.value = false
  }
}

let retryTimer: number | undefined

// 首屏加载：失败（后端未就绪等）时 5 秒后重试直到成功——成功即停，不是轮询
async function bootstrap() {
  try {
    await loadTraders()
    await loadDashboard()
    if (selectedId.value !== null) openStream(selectedId.value)
    errorText.value = null
    if (retryTimer !== undefined) {
      window.clearTimeout(retryTimer)
      retryTimer = undefined
    }
  } catch (err) {
    errorText.value = err instanceof Error ? err.message : '加载交易员失败'
    retryTimer = window.setTimeout(() => void bootstrap(), 5000)
  }
}

onMounted(() => {
  void bootstrap()
  clockTimer = window.setInterval(() => {
    clock.value = Date.now()
  }, 30000)
})

onUnmounted(() => {
  if (retryTimer !== undefined) window.clearTimeout(retryTimer)
  if (clockTimer !== undefined) window.clearInterval(clockTimer)
  closeStream()
})

watch(selectedId, () => {
  historyPage.value = 1
  symbolPage.value = 1
  void loadDashboard()
  if (selectedId.value !== null) openStream(selectedId.value)
})

// 数据刷新后页码越界自动回收（行数变少时）
watch(historyPageCount, (count) => {
  if (historyPage.value > count) historyPage.value = count
})

watch(symbolPageCount, (count) => {
  if (symbolPage.value > count) symbolPage.value = count
})

function onSwitch(value: unknown) {
  const id = Number(value)
  if (Number.isFinite(id) && id > 0) {
    void router.replace({ path: '/dashboard', query: { trader: String(id) } })
  }
}

// —— 展示映射 ——

const ACTION_MAP: Record<string, { label: string; color: string; icon: string }> = {
  buy: { label: '开多', color: 'green', icon: '↑' },
  sell: { label: '开空', color: 'red', icon: '↓' },
  close: { label: '平仓', color: 'arcoblue', icon: '⇄' },
  error: { label: '轮次失败', color: 'red', icon: '!' },
  hold: { label: '观望', color: 'gray', icon: '⏸' },
}

function actionInfo(action: string | null) {
  return (action && ACTION_MAP[action]) || { label: action ?? '—', color: 'gray', icon: '·' }
}

function confidenceColor(confidence: number | null): string {
  if (confidence === null || confidence <= 0) return 'gray'
  if (confidence >= 80) return 'green'
  if (confidence >= 60) return 'gold'
  return 'red'
}

function sideLabel(side: string | null | undefined): string {
  if (!side) return '—'
  if (side.toLowerCase().includes('long') || side === 'Buy') return '多头'
  if (side.toLowerCase().includes('short') || side === 'Sell') return '空头'
  return side
}

function pnlClass(value: number | null | undefined): string {
  if (value === null || value === undefined) return ''
  return value >= 0 ? 'gain' : 'loss'
}

function money(value: number | null | undefined, digits = 2): string {
  if (value === null || value === undefined) return '—'
  return value.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits })
}

function signedMoney(value: number | null | undefined, digits = 2): string {
  if (value === null || value === undefined) return '—'
  return `${value >= 0 ? '+' : ''}${money(value, digits)}`
}

function fmtTime(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return d.toLocaleString('zh-CN', { hour12: false })
}

function fmtClock(iso: string | null): string {
  if (!iso) return '--:--:--'
  const d = new Date(iso)
  return d.toLocaleTimeString('zh-CN', { hour12: false })
}

function fmtPrice(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—'
  const abs = Math.abs(value)
  const digits = abs >= 1000 ? 2 : abs >= 1 ? 4 : abs >= 0.01 ? 6 : 8
  return value.toLocaleString('en-US', { maximumFractionDigits: digits })
}

const runtimeText = computed(() => {
  const info = dashboard.value?.trader
  if (!info?.isRunning || !info.startedAt) return info?.isRunning ? '运行中' : '已停止'
  const minutes = Math.max(0, Math.floor((clock.value - new Date(info.startedAt).getTime()) / 60000))
  return minutes >= 60 ? `${Math.floor(minutes / 60)} 小时 ${minutes % 60} 分钟` : `${minutes} 分钟`
})

const visibleDecisions = computed<DashboardDecision[]>(() =>
  (dashboard.value?.decisions ?? []).slice(0, Math.max(1, Math.min(50, decisionsLimit.value))),
)

// 思维链三段解析（关键信号 / 风险点 / 失效条件，与输出契约同源）；模型未按格式输出时回退整段 Markdown
const COT_SECTIONS = [
  { label: '关键信号', tone: 'cot-chip-signal' },
  { label: '风险点', tone: 'cot-chip-risk' },
  { label: '失效条件', tone: 'cot-chip-invalid' },
] as const

function reasoningSections(text: string): Array<{ label: string; tone: string; text: string }> {
  const starters = COT_SECTIONS.map((s) => s.label).join('|')
  const sections: Array<{ label: string; tone: string; text: string }> = []
  for (const section of COT_SECTIONS) {
    const body = text.match(new RegExp(`${section.label}[：:]\\s*([\\s\\S]*?)(?=(?:${starters})[：:]|$)`))?.[1]?.trim()
    if (body) sections.push({ label: section.label, tone: section.tone, text: body })
  }
  return sections
}

function riskReward(decision: DashboardDecision): number | null {
  // 用 == null 同时兜住 null 与 undefined（帧字段缺失时不参与计算，避免出现 NaN）
  if (decision.stopLossPct == null || decision.takeProfitPct == null) return null
  const sl = Math.abs(decision.stopLossPct)
  const tp = Math.abs(decision.takeProfitPct)
  if (!Number.isFinite(sl) || !Number.isFinite(tp) || sl <= 0) return null
  return Math.round((tp / sl) * 10) / 10
}

const historyMetrics = computed(() => {
  const m = dashboard.value?.history.metrics
  return m
    ? [
        { label: '总交易次数', value: String(m.totalTrades) },
        { label: '胜率', value: `${m.winRate.toFixed(1)}%` },
        { label: '总盈亏', value: signedMoney(m.totalPnl), cls: pnlClass(m.totalPnl) },
        { label: '净盈亏', value: signedMoney(m.netPnl), cls: pnlClass(m.netPnl), hint: '交易所共享接口未提供手续费字段' },
        { label: '盈利因子', value: m.profitFactor.toFixed(2) },
        { label: '盈亏比', value: m.avgLoss !== 0 ? Math.abs(m.avgWin / m.avgLoss).toFixed(2) : '—' },
        { label: '平均盈利', value: `$${money(m.avgWin)}`, cls: 'gain' },
        { label: '平均亏损', value: `$${money(m.avgLoss)}`, cls: 'loss' },
        { label: '最大回撤', value: `${m.maxDrawdownPct.toFixed(1)}%`, cls: 'loss' },
        { label: '夏普比率', value: m.sharpe.toFixed(2), hint: '近似口径：按笔盈亏均值/标准差×√笔数' },
      ]
    : []
})

// 提示词页的复制 / 下载（对齐 nofx 的复制与 .txt 下载按钮）
async function copyText(text: string) {
  try {
    await navigator.clipboard.writeText(text)
    Message.success('已复制')
  } catch {
    Message.error('复制失败（浏览器未授权剪贴板）')
  }
}

function downloadText(filename: string, text: string) {
  const blob = new Blob([text], { type: 'text/plain;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = filename
  anchor.click()
  URL.revokeObjectURL(url)
}

async function closePosition(symbol: string) {
  const id = selectedId.value
  if (id === null || closingSymbol.value) return
  closingSymbol.value = symbol
  try {
    await closeTraderPosition(id, symbol)
    Message.success(`已平仓 ${symbol}`)
    await loadDashboard(true)
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '平仓失败')
  } finally {
    closingSymbol.value = null
  }
}
</script>

<template>
  <div class="page-content">
    <div v-if="traders.length === 0 && !loading" class="page-empty">
      <div class="empty-icon"><IconDashboard /></div>
      <div class="empty-title">暂无交易员</div>
      <div class="empty-sub">请先在 AI 交易员页面创建一个交易员开始使用</div>
      <a-button type="primary" @click="router.push('/traders')">创建交易员</a-button>
    </div>

    <template v-else-if="dashboard">
      <a-alert v-if="errorText" type="error" closable @close="errorText = null" class="page-alert">{{ errorText }}</a-alert>

      <!-- 头部卡（名称行 + 副信息行一体） -->
      <div class="header-card">
        <div class="header-top">
          <div class="avatar">
            <IconRobot />
            <span class="online-dot" :class="dashboard.trader.isRunning ? 'on' : 'off'"></span>
          </div>
          <div class="header-name">
            <div class="trader-title" :title="dashboard.trader.name">{{ dashboard.trader.name }}</div>
            <div class="trader-id">ID: {{ dashboard.trader.id }}</div>
          </div>
          <a-select
            :model-value="selectedId ?? undefined"
            class="trader-switch"
            size="small"
            :style="{ width: '200px', flexShrink: 0 }"
            @update:model-value="onSwitch"
          >
            <a-option v-for="t in traders" :key="t.id" :value="t.id">{{ t.name }}</a-option>
          </a-select>
        </div>
        <div class="header-meta">
        <span class="meta-label">AI模型:</span>
        <a-tag size="small" :color="dashboard.trader.providerName.toLowerCase().includes('deepseek') ? 'gold' : 'purple'">
          {{ dashboard.trader.providerName }}
        </a-tag>
        <span class="meta-label">交易所:</span>
        <span>{{ dashboard.trader.exchangeLabel.toUpperCase() }} - {{ dashboard.trader.accountName }}</span>
        <span class="meta-label">策略:</span>
        <span class="meta-strategy">{{ dashboard.trader.strategyName }}</span>
        <span class="meta-label">运行时间:</span>
        <span>{{ runtimeText }}</span>
        <template v-if="dashboard.trader.positionMode">
          <span class="meta-label">持仓模式:</span>
          <span>{{ dashboard.trader.positionMode }}</span>
        </template>
        <a-tag size="small" :color="dashboard.trader.executionMode === 'live' ? 'red' : 'arcoblue'">
          {{ dashboard.trader.executionMode === 'live' ? '实盘' : '演练' }}
        </a-tag>
        <a-tooltip v-if="!dashboard.trader.capabilityCanTrade && dashboard.trader.executionMode === 'live'" :content="dashboard.trader.capabilityNotes.join('；')">
          <a-tag size="small" color="orange">实盘能力不足</a-tag>
        </a-tooltip>
        </div>
      </div>

      <!-- 调试栏 -->
      <div class="debug-bar">
        <span>系统状态::{{ dashboard.trader.isRunning ? 'ONLINE' : 'STOPPED' }}</span>
        <span class="debug-right">
          最后更新::{{ fmtClock(dashboard.account?.at ?? dashboard.trader.lastCycleAt) }}
          <template v-if="dashboard.account?.success">
            · 净值::{{ money(displayEquity) }}
            · 盈亏::{{ signedMoney(displayTotalPnl) }}
          </template>
        </span>
      </div>

      <a-alert v-if="dashboard.account && !dashboard.account.success" type="warning" class="page-alert">
        账户数据读取失败：{{ dashboard.account.error }}（请检查 API Key 权限/IP 白名单；下方账户指标暂不可用）
      </a-alert>

      <!-- 统计卡 -->
      <div class="stats-row">
        <div class="stat-card">
          <div class="stat-title">总净值</div>
          <div class="stat-value" :class="pnlClass(displayTotalPnl)">
            {{ money(displayEquity) }}<span v-if="displayEquity !== null" class="stat-unit">USDT</span>
          </div>
          <div v-if="displayPnlPct !== null" class="stat-sub" :class="pnlClass(displayTotalPnl)">
            {{ displayPnlPct >= 0 ? '+' : '' }}{{ displayPnlPct.toFixed(2) }}%
          </div>
          <div v-else class="stat-sub">{{ dashboard.account?.success ? '相对初始余额' : '账户读取失败' }}</div>
        </div>
        <div class="stat-card">
          <div class="stat-title">可用余额</div>
          <div class="stat-value">
            {{ money(dashboard.account?.available) }}<span v-if="dashboard.account?.available !== null && dashboard.account?.available !== undefined" class="stat-unit">USDT</span>
          </div>
          <div v-if="dashboard.account?.available !== null && dashboard.account?.available !== undefined && dashboard.account?.equity" class="stat-sub">
            {{ ((dashboard.account.available! / dashboard.account.equity!) * 100).toFixed(1) }}% 空闲
          </div>
        </div>
        <div class="stat-card">
          <div class="stat-title">总盈亏</div>
          <div class="stat-value" :class="pnlClass(displayTotalPnl)">
            {{ signedMoney(displayTotalPnl) }}<span v-if="displayTotalPnl !== null" class="stat-unit">USDT</span>
          </div>
          <div v-if="dashboard.trader.initialBalance !== null" class="stat-sub">初始余额 {{ money(dashboard.trader.initialBalance) }}</div>
          <div v-else class="stat-sub">未设置初始余额</div>
        </div>
        <div class="stat-card">
          <div class="stat-title">持仓</div>
          <div class="stat-value">{{ dashboard.positions.length }}<span class="stat-unit">ACTIVE</span></div>
          <div v-if="dashboard.account?.marginUsedPct !== null && dashboard.account?.marginUsedPct !== undefined" class="stat-sub">
            保证金: {{ dashboard.account.marginUsedPct.toFixed(1) }}%
          </div>
        </div>
      </div>

      <!-- 主内容两列 -->
      <div class="main-grid">
        <div class="main-left">
          <!-- 净值曲线 -->
          <a-card class="chart-card">
            <template #title>账户净值曲线</template>
            <template #extra>
              <a-radio-group v-model="percentMode" type="button" size="mini">
                <a-radio :value="false">USDT</a-radio>
                <a-radio :value="true">%</a-radio>
              </a-radio-group>
            </template>
            <div class="chart-head">
              <template v-if="dashboard.account?.success">
                <span class="chart-equity">{{ money(displayEquity) }} <span class="stat-unit">USDT</span></span>
                <span v-if="displayTotalPnl !== null" class="chart-pnl" :class="pnlClass(displayTotalPnl)">
                  {{ signedMoney(displayTotalPnl) }} USDT
                </span>
              </template>
              <span v-else class="chart-unavailable">账户数据不可用——修复 API Key 权限 / IP 白名单后自动展示净值曲线</span>
            </div>
            <EquityChart
              :samples="dashboard.samples"
              :initial-balance="dashboard.trader.initialBalance"
              :percent-mode="percentMode"
            />
            <div class="chart-footer">
              <span>初始余额：{{ money(dashboard.trader.initialBalance) }}</span>
              <span>当前净值：{{ money(displayEquity) }}</span>
              <span>历史周期：{{ dashboard.samples.length }} 个</span>
              <span>显示范围：{{ percentMode ? '%' : 'USDT' }}</span>
            </div>
          </a-card>

          <!-- 当前持仓 -->
          <a-card class="positions-card">
            <template #title>当前持仓</template>
            <template #extra>
              <a-space :size="6">
                <a-tag v-if="liveConnected" color="green" size="small">实时</a-tag>
                <a-tag v-if="dashboard.positions.length > 0" color="gold" size="small">{{ dashboard.positions.length }} 活跃</a-tag>
              </a-space>
            </template>
            <div v-if="dashboard.positions.length === 0" class="card-empty">当前没有活跃的交易持仓</div>
            <table v-else class="data-table">
              <thead>
                <tr>
                  <th>标的</th>
                  <th>方向</th>
                  <th class="num">数量</th>
                  <th class="num">杠杆</th>
                  <th class="num">{{ priceHeader }}</th>
                  <th class="num">未实现盈亏</th>
                  <th class="num">操作</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="p in dashboard.positions" :key="p.symbol">
                  <td>{{ p.symbol }}</td>
                  <td>
                    <span :class="sideLabel(p.side) === '多头' ? 'gain' : 'loss'">{{ sideLabel(p.side) }}</span>
                  </td>
                  <td class="num">{{ fmtPrice(p.quantity) }}</td>
                  <td class="num">{{ p.leverage ? `${p.leverage}x` : '—' }}</td>
                  <td class="num">{{ realtimeMarks[p.symbol] !== undefined ? fmtPrice(realtimeMarks[p.symbol]) : '—' }}</td>
                  <td class="num" :class="pnlClass(livePnl(p))">{{ signedMoney(livePnl(p)) }}</td>
                  <td class="num">
                    <a-popconfirm
                      :content="`确定要平仓 ${p.symbol} ${sideLabel(p.side)} 吗？将以市价整仓平仓。`"
                      @ok="closePosition(p.symbol)"
                    >
                      <a-button size="mini" :loading="closingSymbol === p.symbol">平仓</a-button>
                    </a-popconfirm>
                  </td>
                </tr>
              </tbody>
            </table>
          </a-card>
        </div>

        <!-- 最近决策 -->
        <a-card class="decisions-card">
          <template #title>最近决策</template>
          <template #extra>
            <span class="decisions-limit">
              最近
              <a-input-number v-model="decisionsLimit" :min="1" :max="50" size="mini" style="width: 56px" />
              个交易周期
            </span>
          </template>

          <div v-if="visibleDecisions.length === 0" class="card-empty">暂无决策记录（交易员启动后每轮落库）</div>
          <div v-else class="decision-list">
            <div v-for="d in visibleDecisions" :key="d.id" class="decision-card">
              <div class="decision-head">
                <div class="decision-icon"><IconRobot /></div>
                <div class="decision-meta">
                  <div class="decision-cycle">周期 #{{ d.id }}</div>
                  <div class="decision-time">{{ fmtTime(d.at) }}</div>
                </div>
                <a-tag size="small" :color="d.status === 'error' ? 'red' : 'green'">{{ d.status === 'error' ? '轮次失败' : '已解析' }}</a-tag>
              </div>

              <div class="token-bar">
                <a-tag v-if="d.elapsedMs" size="small" color="arcoblue">
                  <template #icon><IconClockCircle /></template>
                  {{ (d.elapsedMs / 1000).toFixed(1) }}s
                </a-tag>
                <a-tag v-if="d.inputTokens" size="small" color="purple">
                  <template #icon><IconArrowFall /></template>
                  {{ d.inputTokens.toLocaleString() }}
                </a-tag>
                <a-tag v-if="d.outputTokens" size="small" color="green">
                  <template #icon><IconArrowRise /></template>
                  {{ d.outputTokens.toLocaleString() }}
                </a-tag>
              </div>

              <a-tabs size="small" type="text" class="decision-tabs">
                <a-tab-pane key="coin" title="标的">
                  <div class="action-card">
                    <div class="action-head">
                      <IconClockCircle class="action-clock" />
                      <a-button v-if="d.symbol?.trim()" type="text" size="small" class="action-symbol-btn">
                        {{ d.symbol }}
                      </a-button>
                      <a-tag size="small" :color="actionInfo(d.action).color">{{ actionInfo(d.action).label }}</a-tag>
                      <a-tag v-if="d.confidence !== null && d.confidence > 0" size="small" :color="confidenceColor(d.confidence)">
                        评分 {{ d.confidence }}
                      </a-tag>
                      <span class="status-dot" :class="d.status === 'error' ? 'fail' : 'ok'" :title="d.status === 'error' ? '失败轮次已保存' : '决策已解析并落库'"></span>
                    </div>

                    <div v-if="d.action === 'buy' || d.action === 'sell'" class="price-grid">
                      <div class="price-cell">
                        <div class="price-label">入场价</div>
                        <div class="price-value">{{ fmtPrice(d.entryPrice) }}</div>
                      </div>
                      <div class="price-cell">
                        <div class="price-label">止损</div>
                        <div class="price-value loss">{{ d.stopLossPct !== null ? `-${Math.abs(d.stopLossPct).toFixed(2)}%` : '—' }}</div>
                      </div>
                      <div class="price-cell">
                        <div class="price-label">止盈</div>
                        <div class="price-value gain">{{ d.takeProfitPct !== null ? `+${Math.abs(d.takeProfitPct).toFixed(2)}%` : '—' }}</div>
                      </div>
                      <div class="price-cell">
                        <div class="price-label">杠杆</div>
                        <div class="price-value leverage">{{ d.leverage ? `${d.leverage}x` : '—' }}</div>
                      </div>
                    </div>

                    <div v-if="riskReward(d) !== null" class="rr-row">
                      <span class="price-label">风险回报比</span>
                      <span class="loss">1</span>
                      <span class="rr-colon">:</span>
                      <span class="gain">{{ riskReward(d)!.toFixed(1) }}</span>
                      <div class="rr-track">
                        <div class="rr-fill" :style="{ width: `${Math.min(100, (riskReward(d)! / 5) * 100)}%` }"></div>
                      </div>
                    </div>

                    <a-alert v-if="d.error || d.reason" :type="d.status === 'error' ? 'error' : 'info'" class="reason-alert">{{ d.error ?? d.reason }}</a-alert>
                  </div>
                </a-tab-pane>
                <a-tab-pane key="prompt" title="提示词">
                  <a-collapse :bordered="false" expand-icon-position="right" class="prompt-collapse">
                    <a-collapse-item key="sys">
                      <template #header>
                        <div class="prompt-head">
                          <span class="prompt-label sys">系统提示词</span>
                          <span v-if="d.systemPrompt" class="prompt-actions">
                            <a-button-group size="mini">
                              <a-button type="text" title="复制" @click.stop="copyText(d.systemPrompt)">
                                <template #icon><IconCopy /></template>
                              </a-button>
                              <a-button type="text" title="下载 .txt" @click.stop="downloadText(`决策-${d.id}-系统提示词.txt`, d.systemPrompt)">
                                <template #icon><IconDownload /></template>
                              </a-button>
                            </a-button-group>
                          </span>
                        </div>
                      </template>
                      <MarkdownBlock v-if="d.systemPrompt" :text="d.systemPrompt" />
                      <div v-else class="card-empty">该轮未落库（历史记录）</div>
                    </a-collapse-item>
                    <a-collapse-item key="user">
                      <template #header>
                        <div class="prompt-head">
                          <span class="prompt-label user">用户提示词</span>
                          <span v-if="d.userPrompt" class="prompt-actions">
                            <a-button-group size="mini">
                              <a-button type="text" title="复制" @click.stop="copyText(d.userPrompt)">
                                <template #icon><IconCopy /></template>
                              </a-button>
                              <a-button type="text" title="下载 .txt" @click.stop="downloadText(`决策-${d.id}-用户提示词.txt`, d.userPrompt)">
                                <template #icon><IconDownload /></template>
                              </a-button>
                            </a-button-group>
                          </span>
                        </div>
                      </template>
                      <MarkdownBlock v-if="d.userPrompt" :text="d.userPrompt" />
                      <div v-else class="card-empty">该轮未落库（历史记录）</div>
                    </a-collapse-item>
                    <a-collapse-item key="cot">
                      <template #header>
                        <div class="prompt-head">
                          <span class="prompt-label cot">思维链分析</span>
                          <span v-if="d.reasoning" class="prompt-actions">
                            <a-button-group size="mini">
                              <a-button type="text" title="复制" @click.stop="copyText(d.reasoning)">
                                <template #icon><IconCopy /></template>
                              </a-button>
                              <a-button type="text" title="下载 .txt" @click.stop="downloadText(`决策-${d.id}-思维链.txt`, d.reasoning)">
                                <template #icon><IconDownload /></template>
                              </a-button>
                            </a-button-group>
                          </span>
                        </div>
                      </template>
                      <template v-if="d.reasoning">
                        <template v-if="reasoningSections(d.reasoning).length > 0">
                          <div v-for="section in reasoningSections(d.reasoning)" :key="section.label" class="cot-section">
                            <span class="cot-chip" :class="section.tone">{{ section.label }}</span>
                            <MarkdownBlock :text="section.text" />
                          </div>
                        </template>
                        <MarkdownBlock v-else :text="d.reasoning" />
                      </template>
                      <div v-else class="card-empty">该轮未保存思维链</div>
                    </a-collapse-item>
                  </a-collapse>
                </a-tab-pane>
              </a-tabs>
            </div>
          </div>
        </a-card>
      </div>

      <!-- 历史仓位统计 -->
      <a-card class="history-card">
        <template #title>历史仓位</template>
        <template #extra>
          <a-tag v-if="!dashboard.history.success" size="small" color="gray">{{ dashboard.history.error ?? '暂无历史记录' }}</a-tag>
        </template>

        <div v-if="dashboard.history.metrics.totalTrades === 0" class="card-empty">
          暂无历史仓位记录（完成一轮开仓→平仓后按真实成交回报统计）
        </div>
        <template v-else>
          <div v-if="dashboard.history.note" class="history-note">{{ dashboard.history.note }}</div>
          <div class="metrics-band">
            <div v-for="m in historyMetrics" :key="m.label" class="metric-cell" :title="m.hint ?? ''">
              <div class="metric-label">{{ m.label }}</div>
              <div class="metric-value" :class="m.cls">{{ m.value }}</div>
            </div>
          </div>

          <div v-if="directionCards.length > 0" class="direction-row">
            <div v-for="dir in directionCards" :key="dir.side" class="direction-card">
              <div class="direction-head">
                <span>{{ dir.label }}</span>
                <span class="direction-pnl" :class="dir.empty ? '' : pnlClass(dir.totalPnl)">
                  {{ dir.empty ? '—' : signedMoney(dir.totalPnl) }}
                </span>
              </div>
              <div class="direction-stats">
                <span>交易次数 {{ dir.count }}</span>
                <span :class="dir.empty ? '' : 'gain'">
                  胜率 {{ dir.winRate === null ? '—' : `${dir.winRate.toFixed(0)}%` }}
                </span>
                <span :class="dir.empty ? '' : pnlClass(dir.avgPnl)">
                  平均盈亏 {{ dir.avgPnl === null ? '—' : signedMoney(dir.avgPnl) }}
                </span>
              </div>
            </div>
          </div>

          <a-collapse :bordered="false" expand-icon-position="right" class="history-collapse">
            <a-collapse-item v-if="allHistoryRows.length > 0" key="symbols">
              <template #header>品种表现</template>
            <div class="filter-row">
              <a-select
                v-model="symbolFilterSymbol"
                size="mini"
                placeholder="全部标的"
                allow-clear
                :style="{ width: '160px' }"
              >
                <a-option v-for="s in symbolOptions" :key="s" :value="s">{{ s }}</a-option>
              </a-select>
            </div>
            <table class="data-table">
              <thead>
                <tr><th>标的</th><th class="num">交易次数</th><th class="num">胜率</th><th class="num">盈亏</th></tr>
              </thead>
              <tbody>
                <tr v-if="pagedSymbols.length === 0">
                  <td colspan="4" class="table-empty">无匹配记录</td>
                </tr>
                <tr v-for="(s, i) in pagedSymbols" :key="(symbolPage - 1) * symbolPageSize + i">
                  <td>{{ s.symbol }}</td>
                  <td class="num">{{ s.count }}</td>
                  <td class="num gain">{{ s.winRate.toFixed(0) }}%</td>
                  <td class="num" :class="pnlClass(s.totalPnl)">{{ signedMoney(s.totalPnl) }}</td>
                </tr>
              </tbody>
            </table>
            <div v-if="filteredSymbolStats.length > 0" class="history-pagination">
              <a-pagination
                v-model:current="symbolPage"
                :total="filteredSymbolStats.length"
                :page-size="symbolPageSize"
                :page-size-options="HISTORY_PAGE_SIZE_OPTIONS"
                size="small"
                show-total
                show-page-size
                @page-size-change="onSymbolPageSizeChange"
              />
            </div>
            </a-collapse-item>

            <a-collapse-item key="positions">
              <template #header>仓位历史</template>
            <div class="filter-row">
              <a-select
                v-model="historyFilterSymbol"
                size="mini"
                placeholder="全部标的"
                allow-clear
                :style="{ width: '160px' }"
              >
                <a-option v-for="s in symbolOptions" :key="s" :value="s">{{ s }}</a-option>
              </a-select>
              <a-select
                v-model="historyFilterSide"
                size="mini"
                placeholder="全部方向"
                allow-clear
                :style="{ width: '120px' }"
              >
                <a-option v-for="o in SIDE_FILTER_OPTIONS" :key="o.value" :value="o.value">{{ o.label }}</a-option>
              </a-select>
            </div>
            <table class="data-table">
              <thead>
                <tr>
                  <th>标的</th><th>方向</th>
                  <th class="num">数量</th><th class="num">杠杆</th>
                  <th class="num">入场均价</th><th class="num">平仓均价</th>
                  <th class="num">已实现盈亏</th><th class="num">手续费</th><th class="num">平仓时间</th>
                </tr>
              </thead>
              <tbody>
                <tr v-if="pagedHistoryRows.length === 0">
                  <td colspan="9" class="table-empty">无匹配记录</td>
                </tr>
                <tr v-for="(r, i) in pagedHistoryRows" :key="(historyPage - 1) * historyPageSize + i">
                  <td>{{ r.symbol }}</td>
                  <td>{{ sideLabel(r.side) }}</td>
                  <td class="num">{{ fmtPrice(r.quantity) }}</td>
                  <td class="num">{{ r.leverage ? `${r.leverage}x` : '—' }}</td>
                  <td class="num">{{ fmtPrice(r.entryPrice) }}</td>
                  <td class="num">{{ fmtPrice(r.exitPrice) }}</td>
                  <td class="num" :class="pnlClass(r.realizedPnl)">{{ signedMoney(r.realizedPnl) }}</td>
                  <td class="num fee-cell">{{ r.fee !== null ? money(r.fee, 4) : '—' }}</td>
                  <td class="num">{{ fmtTime(r.closeTime) }}</td>
                </tr>
              </tbody>
            </table>
            <div v-if="filteredHistoryRows.length > 0" class="history-pagination">
              <a-pagination
                v-model:current="historyPage"
                :total="filteredHistoryRows.length"
                :page-size="historyPageSize"
                :page-size-options="HISTORY_PAGE_SIZE_OPTIONS"
                size="small"
                show-total
                show-page-size
                @page-size-change="onHistoryPageSizeChange"
              />
            </div>
            </a-collapse-item>
          </a-collapse>
        </template>

        <a-collapse v-if="dashboard.recentOrders.length > 0" :bordered="false" expand-icon-position="right" class="history-collapse">
          <a-collapse-item key="orders">
            <template #header>执行审计</template>
          <table class="data-table">
            <thead>
              <tr><th>时间</th><th>标的</th><th>动作</th><th class="num">数量</th><th class="num">价格</th><th>结果</th><th>说明</th></tr>
            </thead>
            <tbody>
              <tr v-for="o in dashboard.recentOrders" :key="o.id">
                <td>{{ fmtTime(o.at) }}</td>
                <td>{{ o.symbol ?? '—' }}</td>
                <td>{{ actionInfo(o.action).label }}</td>
                <td class="num">{{ fmtPrice(o.quantity) }}</td>
                <td class="num">{{ fmtPrice(o.price) }}</td>
                <td>
                  <a-tag size="small" :color="o.success ? 'green' : 'red'">{{ o.success ? '成功' : '失败' }}</a-tag>
                </td>
                <td class="note-cell">{{ o.note ?? o.error ?? '—' }}</td>
              </tr>
            </tbody>
          </table>
          </a-collapse-item>
        </a-collapse>
      </a-card>
    </template>

    <div v-else-if="loading" class="page-loading">
      <LuckyCatScene class="loading-cat" />
      <span class="loading-text">正在准备看板…</span>
    </div>
  </div>
</template>

<style scoped>
.page-content {
  width: 100%;
  max-width: 1200px;
  margin: 0 auto;
  padding: 24px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.page-empty,
.page-loading {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 120px 0;
}

.loading-cat {
  width: 140px;
  height: 140px;
}

.loading-text {
  color: var(--color-text-3);
  font-size: 13px;
}

.empty-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 96px;
  height: 96px;
  border: 2px solid rgba(240, 185, 11, 0.35);
  border-radius: 50%;
  color: rgb(var(--primary-6));
  font-size: 44px;
}

.empty-title {
  font-size: 20px;
  color: var(--color-text-3);
}

.empty-sub {
  font-size: 13px;
  color: var(--color-text-3);
  margin-bottom: 8px;
}

.page-alert {
  margin: 0;
}

.header-card {
  display: flex;
  flex-direction: column;
  padding: 18px 24px 0;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-2);
}

.header-top {
  display: flex;
  align-items: center;
  gap: 14px;
  padding-bottom: 14px;
}

.avatar {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 48px;
  height: 48px;
  border-radius: 12px;
  background: var(--color-fill-2);
  color: rgb(var(--primary-6));
  font-size: 24px;
  flex-shrink: 0;
}

.online-dot {
  position: absolute;
  right: -3px;
  bottom: -3px;
  width: 12px;
  height: 12px;
  border-radius: 50%;
  border: 2px solid var(--color-bg-2);
}

.online-dot.on {
  background: rgb(var(--success-6));
}

.online-dot.off {
  background: rgb(var(--danger-6));
}

.header-name {
  flex: 1;
  min-width: 0;
}

.trader-title {
  font-size: 20px;
  font-weight: 600;
  color: var(--color-text-1);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.trader-id {
  font-size: 12px;
  color: var(--color-text-3);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}

/* a-select 根节点不接收 scoped 属性：宽度用内联 :style 保证（本项目既有约定） */
.trader-switch {
  width: 200px;
}

.header-meta {
  display: flex;
  align-items: center;
  gap: 8px 10px;
  flex-wrap: wrap;
  padding: 12px 0;
  border-top: 1px solid var(--color-border-1);
  font-size: 13px;
  color: var(--color-text-2);
}

.meta-label {
  color: var(--color-text-3);
  font-size: 12px;
}

.meta-strategy {
  color: rgb(var(--primary-6));
}

.debug-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 12px;
  border: 1px solid var(--color-border-2);
  border-radius: 6px;
  background: var(--color-fill-1);
  font-size: 10px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  color: var(--color-text-3);
}

.debug-right {
  display: flex;
  gap: 16px;
}

.stats-row {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 16px;
}

@media (max-width: 1000px) {
  .stats-row {
    grid-template-columns: repeat(2, 1fr);
  }
}

.stat-card {
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-2);
  padding: 14px 16px;
  min-height: 100px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.stat-title {
  font-size: 11px;
  letter-spacing: 0.08em;
  color: var(--color-text-3);
}

.stat-value {
  font-size: 22px;
  font-weight: 700;
  color: var(--color-text-1);
  font-variant-numeric: tabular-nums;
}

.chart-unavailable {
  font-size: 13px;
  color: var(--color-text-3);
}

.stat-unit {
  font-size: 12px;
  font-weight: 400;
  color: var(--color-text-3);
  margin-left: 4px;
}

.stat-sub {
  font-size: 12px;
  color: var(--color-text-3);
}

.gain {
  color: rgb(var(--success-6));
}

.loss {
  color: rgb(var(--danger-6));
}

.main-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.6fr) minmax(0, 1fr);
  gap: 16px;
  align-items: stretch;
}

@media (max-width: 1000px) {
  .main-grid {
    grid-template-columns: 1fr;
  }
}

.main-left {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-width: 0;
}

/* 图表卡吃掉左列剩余高度，两列底部对齐；body 用 flex 链撑满（勿用 height:100%——会把卡头高度也算进去导致溢出裁切） */
.main-left > .chart-card {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.chart-card :deep(.arco-card-header) {
  flex-shrink: 0;
}

.chart-card :deep(.arco-card-body) {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.chart-card :deep(.arco-card-body) .equity-chart {
  flex: 1;
  min-height: 0;
}

.chart-head {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 36px;
  margin-bottom: 8px;
}

.chart-equity {
  font-size: 26px;
  font-weight: 700;
  color: var(--color-text-1);
}

.chart-pnl {
  font-size: 13px;
  font-weight: 600;
}

.chart-footer {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 8px;
  margin-top: 10px;
  padding-top: 10px;
  border-top: 1px solid var(--color-border-1);
  font-size: 12px;
  color: var(--color-text-2);
}

.card-empty {
  padding: 32px 0;
  text-align: center;
  font-size: 13px;
  color: var(--color-text-3);
}

.data-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
}

.data-table th {
  text-align: left;
  font-size: 11px;
  font-weight: 600;
  color: var(--color-text-3);
  padding: 4px 4px 10px;
  border-bottom: 1px solid var(--color-border-1);
  white-space: nowrap;
}

.data-table td {
  padding: 10px 4px;
  border-bottom: 1px solid var(--color-border-1);
  color: var(--color-text-1);
  white-space: nowrap;
}

.data-table .num {
  text-align: right;
}

.decisions-card {
  min-width: 0;
}

.decisions-limit {
  font-size: 11px;
  color: var(--color-text-3);
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.decision-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
  max-height: 720px;
  overflow-y: auto;
}

.decision-card {
  border: 1px solid var(--color-border-2);
  border-radius: 12px;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 10px;
  background: var(--color-bg-1);
}

.decision-head {
  display: flex;
  align-items: center;
  gap: 10px;
}

.decision-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  border-radius: 8px;
  background: rgba(240, 185, 11, 0.15);
  color: rgb(var(--primary-6));
  font-size: 20px;
}

.decision-meta {
  flex: 1;
  min-width: 0;
}

.decision-cycle {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.decision-time {
  font-size: 11px;
  color: var(--color-text-3);
}

.token-bar {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.action-card {
  border: 1px solid var(--color-border-1);
  border-radius: 8px;
  padding: 10px;
  display: flex;
  flex-direction: column;
  gap: 10px;
  background: var(--color-bg-2);
}

.action-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.action-icon {
  font-size: 16px;
}

.action-clock {
  font-size: 18px;
  color: var(--color-text-3);
}

.action-symbol-btn {
  font-size: 15px;
  font-weight: 700;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  padding: 0;
  height: auto;
}

.status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  margin-left: auto;
}

.status-dot.ok {
  background: rgb(var(--success-6));
}

.status-dot.fail {
  background: rgb(var(--danger-6));
}

.decision-tabs {
  margin-top: 2px;
}

/* 提示词页（对齐 nofx：三个折叠区 + 每项复制/下载 + nofx 同款正文框） */
.prompt-collapse {
  background: transparent;
}

.prompt-collapse :deep(.arco-collapse-item-header) {
  background: transparent;
  padding-left: 0;
  padding-right: 24px;
  border-bottom: 1px solid var(--color-border-1);
}

/* Arco 标题容器默认为内容宽度：拉伸后复制/下载图标组才会贴到右端（离文字保持间距） */
.prompt-collapse :deep(.arco-collapse-item-header-title) {
  flex: 1;
  min-width: 0;
}

.prompt-collapse :deep(.arco-collapse-item-content) {
  background: transparent;
  padding-left: 0;
}

.prompt-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
}

.prompt-label {
  font-size: 12px;
  font-weight: 600;
}

.prompt-label.sys {
  color: #a78bfa;
}

.prompt-label.user {
  color: #f0b90b;
}

.prompt-label.cot {
  color: rgb(var(--primary-6));
}

.prompt-actions {
  display: flex;
  gap: 2px;
  margin-left: 12px;
  margin-right: 10px;
}

.prompt-body {
  font-size: 12px;
  color: var(--color-text-2);
  background: var(--color-bg-1);
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  padding: 12px;
  line-height: 1.7;
  max-height: 360px;
  overflow-y: auto;
  white-space: pre-wrap;
}

/* 思维链三段：标签块 + Markdown 正文（模型未按格式输出时整段渲染，无标签） */
.cot-section {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.cot-section + .cot-section {
  margin-top: 12px;
}

.cot-chip {
  align-self: flex-start;
  padding: 0 6px;
  border-radius: 4px;
  font-size: 11px;
  font-weight: 600;
  line-height: 18px;
}

.cot-chip-signal {
  color: #2f6fdb;
  background: rgba(47, 111, 219, 0.12);
}

.cot-chip-risk {
  color: #c97816;
  background: rgba(201, 120, 22, 0.12);
}

.cot-chip-invalid {
  color: #6b7280;
  background: rgba(107, 114, 128, 0.12);
}

.reason-alert {
  margin-top: 8px;
}

.reason-alert :deep(.arco-alert-content) {
  font-size: 12px;
}

.action-symbol {
  font-size: 14px;
  font-weight: 700;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  color: var(--color-text-1);
}

.price-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 8px;
  border-top: 1px solid var(--color-border-1);
  padding-top: 8px;
}

.price-cell {
  text-align: center;
}

.price-label {
  font-size: 10px;
  color: var(--color-text-3);
}

.price-value {
  font-size: 12px;
  font-weight: 600;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  color: var(--color-text-1);
}

.price-value.leverage {
  color: rgb(var(--primary-6));
}

.rr-row {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
}

.rr-colon {
  color: var(--color-text-3);
}

.rr-track {
  flex: 1;
  height: 6px;
  border-radius: 3px;
  background: var(--color-fill-2);
  overflow: hidden;
}

.rr-fill {
  height: 100%;
  border-radius: 3px;
  background: rgb(var(--success-6));
}

.decision-reason {
  font-size: 12px;
  color: var(--color-text-2);
  background: var(--color-fill-1);
  border-radius: 6px;
  padding: 8px 10px;
  line-height: 1.6;
}

.reasoning-collapse {
  background: transparent;
}

.reasoning-text {
  font-size: 12px;
  color: var(--color-text-2);
  background: var(--color-fill-1);
  border-radius: 6px;
  padding: 10px;
  line-height: 1.7;
  max-height: 320px;
  overflow-y: auto;
  white-space: pre-wrap;
}

.history-card {
  min-height: 200px;
}

.history-note {
  font-size: 12px;
  color: var(--color-text-3);
  background: var(--color-fill-1);
  border-radius: 6px;
  padding: 6px 10px;
  margin-bottom: 12px;
}

.metrics-band {
  display: grid;
  grid-template-columns: repeat(5, 1fr);
  gap: 8px;
  margin-bottom: 14px;
}

@media (max-width: 1000px) {
  .metrics-band {
    grid-template-columns: repeat(2, 1fr);
  }
}

.metric-cell {
  border: 1px solid var(--color-border-1);
  border-radius: 6px;
  background: var(--color-fill-1);
  padding: 10px;
  text-align: center;
}

.metric-label {
  font-size: 10px;
  color: var(--color-text-3);
  margin-bottom: 4px;
}

.metric-value {
  font-size: 16px;
  font-weight: 700;
  color: var(--color-text-1);
}

.direction-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
  margin-bottom: 14px;
}

.direction-card {
  border: 1px solid var(--color-border-1);
  border-radius: 8px;
  background: var(--color-fill-1);
  padding: 12px 14px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.direction-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.direction-pnl {
  font-size: 20px;
  font-weight: 700;
}

.direction-stats {
  display: flex;
  gap: 14px;
  font-size: 12px;
  color: var(--color-text-3);
}

.symbol-block {
  margin-top: 12px;
}

/* 品种表现 / 仓位历史 折叠区（默认收起） */
.history-collapse {
  margin-top: 12px;
  background: transparent;
}

.history-collapse :deep(.arco-collapse-item-header) {
  background: transparent;
  border-bottom: 1px solid var(--color-border-1);
  padding-left: 0;
  padding-right: 24px;
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-2);
}

.history-collapse :deep(.arco-collapse-item-content) {
  background: transparent;
  padding-left: 0;
}

/* 两组折叠区相邻时贴齐（品种表现/仓位历史 与 执行审计） */
.history-collapse + .history-collapse {
  margin-top: 0;
}

.filter-row {
  display: flex;
  gap: 8px;
  margin-bottom: 8px;
}

.table-empty {
  text-align: center;
  color: var(--color-text-3);
  padding: 18px 0;
}

.history-pagination {
  display: flex;
  justify-content: flex-end;
  margin-top: 10px;
}

.block-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-2);
  margin-bottom: 6px;
}

.note-cell {
  max-width: 280px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--color-text-3);
}
</style>
