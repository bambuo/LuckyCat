<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Message } from '@arco-design/web-vue'
import { IconDashboard, IconRobot } from '@arco-design/web-vue/es/icon'
import {
  ApiRequestError,
  closePosition as apiClosePosition,
  fetchTraderDashboard,
  getCycle,
  listTraders,
  openTraderStream,
  startTrader,
  stopTrader,
} from '../api'
import type { ConnectionState } from '../api'
import LuckyCatScene from '../components/trader/LuckyCatScene.vue'
import CycleTimeline from '../components/cycle/CycleTimeline.vue'
import OrderCard from '../components/cycle/OrderCard.vue'
import ProposalCard from '../components/cycle/ProposalCard.vue'
import VerdictCard from '../components/cycle/VerdictCard.vue'
import EquityChart from '../components/trader/EquityChart.vue'
import type {
  CycleDetailDto,
  CycleDto,
  CycleEvent,
  CycleOrderDto,
  CycleStatus,
  DashboardSample,
  DashboardSymbolStatDto,
  ExecutionMode,
  OrderStatus,
  ProposalAction,
  ProposalDto,
  RecentOrderDto,
  RiskDecision,
  StageDto,
  StageRole,
  StageStatus,
  Trader,
  TraderDashboardDto,
  TriggerType,
  VerdictDto,
} from '../types'

const route = useRoute()
const router = useRouter()

// 新链路看板（Contracts/TraderContracts.cs TraderDashboardDto）：金额/数量/百分比均为字符串。
const traders = ref<Trader[]>([])
const dashboard = ref<TraderDashboardDto | null>(null)
// 最新周期详情（dashboard.latestCycle；SSE 增量合并；新周期按事件里缺失的字段用 getCycle 补齐）。
const latestCycle = ref<CycleDetailDto | null>(null)
const cycleLoading = ref(false)
const cycleErrorText = ref<string | null>(null)
const loading = ref(false)
const errorText = ref<string | null>(null)
const percentMode = ref(false)
const actionBusy = ref(false)

// 走动的时钟：像「运行时间」这类随时间增长的显示，computed 的依赖不含 Date.now()，
// 事件驱动（无定时拉取）下不会自行重算——显式依赖这个每 30 秒更新的 ref（UI 时钟，非数据轮询）
const clock = ref(Date.now())
let clockTimer: number | undefined
const closingSymbol = ref<string | null>(null)

// SSE（openTraderStream）：状态横幅 + 按周期/阶段/提案/裁决/订单/工具/权益增量合并。
// 断线（reconnecting/closed/error）后重拉一次 dashboard + 最新周期详情，再恢复增量合并；
// 重拉期间到达的帧先缓冲，快照落地后按幂等键合并（不重复应用、不让迟到帧回退终态）。
const streamStatus = ref<ConnectionState>('connecting')
const STREAM_STATUS_META: Record<ConnectionState, { label: string; color: string }> = {
  connecting: { label: '连接中', color: 'arcoblue' },
  open: { label: '已连接', color: 'green' },
  reconnecting: { label: '重连中', color: 'gold' },
  error: { label: '连接失败', color: 'red' },
  closed: { label: '连接失败', color: 'gray' },
}
const streamStatusMeta = computed(() => STREAM_STATUS_META[streamStatus.value])

interface ToolChip {
  key: string
  stageId: string
  tool: string
  success: boolean
  error: string | null
  errorCode: string | null
  elapsedMs: number
  at: string
}

const stageTools = ref<Record<string, ToolChip[]>>({})
const malformedFrameCount = ref(0)
const streamStale = ref(false)
let disposeStream: (() => void) | null = null
let streamTraderId: number | null = null
let streamHasOpened = false
let needsResync = false
let resyncInFlight = false
let lastFrameAt = 0
let lastPositionHistoryRefreshAt = 0
let closedRebuildUsed = false
let closedRebuildTimer: number | undefined
let livenessTimer: number | undefined
let dashboardGeneration = 0
// 重连重同步期间到达的帧按「代次」缓冲：交易员切换后旧代次的帧不得落到新看板
const pendingEvents: Array<{ generation: number; event: CycleEvent }> = []

// —— 数值展示（后端金额/百分比/数量一律字符串：只解析做格式化，不做业务计算）——

function toNumber(value: string | number | null | undefined): number | null {
  if (value === null || value === undefined || value === '') return null
  const parsed = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(parsed) ? parsed : null
}

/** 定点小数（字符串来源绝不直接 .toFixed 原值）。 */
function fmtNumber(value: string | number | null | undefined, digits = 2): string {
  const n = toNumber(value)
  return n === null ? '—' : n.toFixed(digits)
}

/** 百分比（后端按百分数口径返回，如 "62.5" → "62.5%"）。 */
function fmtPct(value: string | number | null | undefined, digits = 1): string {
  const n = toNumber(value)
  return n === null ? '—' : `${n.toFixed(digits)}%`
}

/** 带符号百分比（盈亏/涨跌幅展示）。 */
function signedPct(value: string | number | null | undefined, digits = 2): string {
  const n = toNumber(value)
  return n === null ? '—' : `${n >= 0 ? '+' : ''}${n.toFixed(digits)}%`
}

/** 金额（千分位 + 固定小数）；解析失败显示 —。 */
function fmtAmount(value: string | number | null | undefined, digits = 2): string {
  const n = toNumber(value)
  if (n === null) return '—'
  return n.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits })
}

function signedAmount(value: string | number | null | undefined, digits = 2): string {
  const n = toNumber(value)
  if (n === null) return '—'
  return `${n >= 0 ? '+' : ''}${fmtAmount(n, digits)}`
}

/** 占用/空闲比例：需要算术，解析 → 有限性/分母守卫 → 定点。 */
function ratioPct(part: string | number | null | undefined, whole: string | number | null | undefined): string {
  const p = toNumber(part)
  const w = toNumber(whole)
  if (p === null || w === null || w <= 0) return '—'
  return `${((p / w) * 100).toFixed(1)}%`
}

/** 两个字符串数值的比值（盈亏比等展示；分母为 0 或不可解析 → —）。 */
function ratioOf(numerator: string | null, denominator: string | null): string {
  const a = toNumber(numerator)
  const b = toNumber(denominator)
  if (a === null || b === null || b === 0) return '—'
  return Math.abs(a / b).toFixed(2)
}

function text(value: string | null | undefined): string {
  return value === null || value === undefined || value === '' ? '—' : value
}

function pnlClass(value: string | number | null | undefined): string {
  const n = toNumber(value)
  if (n === null) return ''
  return n >= 0 ? 'gain' : 'loss'
}

function strOrNull(value: unknown): string | null {
  return typeof value === 'string' ? value : null
}

function isOneOf<T extends string>(value: unknown, allowed: readonly T[]): value is T {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value)
}

const CYCLE_STATUSES: readonly CycleStatus[] = ['running', 'completed', 'failed', 'skipped', 'aborted']
const MODES: readonly ExecutionMode[] = ['dry', 'live']
const TRIGGERS: readonly TriggerType[] = ['schedule', 'manual', 'test']
const STAGE_ROLES: readonly StageRole[] = ['analyst', 'trader', 'risk', 'executor', 'reviewer']
const STAGE_STATUSES: readonly StageStatus[] = ['completed', 'failed', 'skipped']
const PROPOSAL_ACTIONS: readonly ProposalAction[] = ['open', 'close', 'hold']
const RISK_DECISIONS: readonly RiskDecision[] = ['approved', 'rejected']
const ORDER_STATUSES: readonly OrderStatus[] = ['submitted', 'filled', 'partial', 'failed', 'unknown', 'cancelled']

// —— SSE：连接生命周期 ——

function applyEventSafely(event: CycleEvent) {
  try {
    applyEvent(event)
  } catch {
    // 防御：单帧结构异常不拖垮页面（api 层已拦截非法 JSON）
  }
}

function disposeCurrentStream() {
  disposeStream?.()
  disposeStream = null
  streamTraderId = null
}

function connectStream(traderId: number, resume = false) {
  disposeCurrentStream()
  streamTraderId = traderId
  // resume=重建：已有首屏快照，重建后的 open 走一次补拉
  streamHasOpened = resume
  streamStatus.value = 'connecting'
  lastFrameAt = Date.now()
  streamStale.value = false
  const generation = dashboardGeneration
  disposeStream = openTraderStream(
    traderId,
    (event) => {
      if (streamTraderId !== traderId || generation !== dashboardGeneration) return
      // 任何帧（含 ping）都算存活
      lastFrameAt = Date.now()
      streamStale.value = false
      // 交易员切换瞬间旧看板仍在内存：不把新流事件落到旧交易员的快照上
      if (dashboard.value && dashboard.value.trader.id !== traderId) return
      if (resyncInFlight) {
        pendingEvents.push({ generation, event })
        return
      }
      applyEventSafely(event)
    },
    {
      onStatus: (state) => {
        if (streamTraderId === traderId && generation === dashboardGeneration) onStreamStatus(state)
      },
      onError: () => {
        // 坏帧已由 api 层跳过且流继续；计数后统一显示一条轻提示，不弹 toast
        malformedFrameCount.value += 1
      },
    },
  )
}

function disconnectStream() {
  disposeCurrentStream()
  streamHasOpened = false
  needsResync = false
  closedRebuildUsed = false
  if (closedRebuildTimer !== undefined) {
    window.clearTimeout(closedRebuildTimer)
    closedRebuildTimer = undefined
  }
  pendingEvents.length = 0
  stageTools.value = {}
  streamStale.value = false
  lastFrameAt = 0
}

function onStreamStatus(state: ConnectionState) {
  streamStatus.value = state
  if (state === 'open') {
    lastFrameAt = Date.now()
    streamStale.value = false
    // 首屏快照在 connect 之前已完成；只有断线后的 open 才补拉，避免同一快照拉两次。
    const shouldResync = streamHasOpened && needsResync
    streamHasOpened = true
    if (shouldResync) void resyncStream()
    return
  }
  if (state === 'reconnecting' || state === 'error' || state === 'closed') {
    needsResync = true
    if (state === 'closed') scheduleStreamRebuild()
  }
}

// EventSource 已放弃（readyState=CLOSED）时重建一次；同一交易员只自动重建一次，避免失败循环。
function scheduleStreamRebuild() {
  const traderId = streamTraderId
  if (traderId === null || closedRebuildUsed) return
  closedRebuildUsed = true
  closedRebuildTimer = window.setTimeout(() => {
    closedRebuildTimer = undefined
    if (selectedId.value !== traderId || streamTraderId !== traderId) return
    connectStream(traderId, true)
  }, 2000)
}

// 重连重同步：回到 open 后才执行（dashboard + 最新周期详情各拉一次）；
// 重拉期间到达的帧缓冲，快照落地后幂等合并。
async function resyncStream() {
  const traderId = streamTraderId
  const generation = dashboardGeneration
  if (traderId === null || selectedId.value !== traderId || resyncInFlight) return
  resyncInFlight = true
  try {
    const ok = await loadDashboard(true)
    if (!ok) {
      needsResync = true
      return
    }
    if (generation !== dashboardGeneration || selectedId.value !== traderId) return
    const cycleId = latestCycle.value?.cycle.id
    if (cycleId) await loadCycleDetail(cycleId, true)
    if (generation === dashboardGeneration && selectedId.value === traderId) needsResync = false
  } finally {
    resyncInFlight = false
    const buffered = pendingEvents.splice(0)
    for (const entry of buffered) {
      // 旧代次的帧丢弃；当前代次的帧必须补上（交易员切换发生在旧重同步期间时不能丢新流事件）
      if (entry.generation !== dashboardGeneration) continue
      if (selectedId.value !== streamTraderId) continue
      applyEventSafely(entry.event)
    }
  }
}

// —— SSE：按事件类型增量合并（字段显式映射，不 spread 原始载荷）——

function applyEvent(event: CycleEvent) {
  switch (event.type) {
    case 'state':
      applyState(event)
      break
    case 'cycle':
      applyCycle(event)
      break
    case 'stage':
      applyStage(event)
      break
    case 'proposal':
      applyProposal(event)
      break
    case 'verdict':
      applyVerdict(event)
      break
    case 'order':
      applyOrder(event)
      break
    case 'tool':
      applyTool(event)
      break
    case 'equity':
      applyEquity(event)
      break
    case 'position-history':
      applyPositionHistory(event)
      break
    case 'ping':
      // 心跳帧：仅保活，无业务含义
      break
  }
}

function applyState(event: Extract<CycleEvent, { type: 'state' }>) {
  const info = dashboard.value?.trader
  if (!info) return
  if (typeof event.isRunning === 'boolean') {
    info.isRunning = event.isRunning
    if (!event.isRunning) info.startedAt = null
  }
  info.lastCycleStatus = isOneOf(event.lastCycleStatus, CYCLE_STATUSES) ? event.lastCycleStatus : null
  info.lastCycleAt = strOrNull(event.lastCycleAt)
}

function applyCycle(event: Extract<CycleEvent, { type: 'cycle' }>) {
  const id = event.cycleId
  if (typeof id !== 'string' || id === '') return
  if (!isOneOf(event.mode, MODES) || !isOneOf(event.trigger, TRIGGERS) || !isOneOf(event.status, CYCLE_STATUSES)) {
    return
  }
  const current = latestCycle.value
  const same = current?.cycle.id === id
  // 迟到的旧周期帧：不覆盖当前更新的周期
  if (current && !same && current.cycle.startedAt > event.startedAt) return
  // 快照已终态后重放的 running 帧：不把状态回退
  if (same && current.cycle.status !== 'running' && event.status === 'running') return

  const cycle: CycleDto = {
    id,
    traderId: typeof event.traderId === 'number' ? event.traderId : same ? current.cycle.traderId : null,
    // 事件不含 strategyId：新周期先用 0 占位（CycleTimeline 不展示该字段），随后 getCycle 覆盖
    strategyId: same ? current.cycle.strategyId : 0,
    providerId: same ? current.cycle.providerId : null,
    exchangeAccountId: same ? current.cycle.exchangeAccountId : null,
    mode: event.mode,
    trigger: event.trigger,
    status: event.status,
    startedAt: event.startedAt,
    completedAt: event.completedAt ?? null,
    errorCode: same ? current.cycle.errorCode : null,
    error: event.error ?? null,
    inputTokens: same ? current.cycle.inputTokens : null,
    outputTokens: same ? current.cycle.outputTokens : null,
    elapsedMs: same ? current.cycle.elapsedMs : null,
  }

  if (same) {
    latestCycle.value = { ...current, cycle }
  } else {
    latestCycle.value = { cycle, stages: [], proposal: null, verdict: null, orders: [] }
    stageTools.value = {}
    // 新周期：仅此时拉取详情（阶段/提案/裁决/订单），不是每帧都拉
    void loadCycleDetail(id, true)
  }
  if (dashboard.value) {
    dashboard.value.trader.lastCycleAt = event.startedAt
    dashboard.value.trader.lastCycleStatus = event.status
  }
}

function applyStage(event: Extract<CycleEvent, { type: 'stage' }>) {
  const cycle = latestCycle.value
  if (!cycle || event.cycleId !== cycle.cycle.id) return
  if (!isOneOf(event.role, STAGE_ROLES) || !isOneOf(event.status, STAGE_STATUSES)) return
  const stageId = typeof event.stageId === 'string' ? event.stageId : ''
  if (!stageId) return
  const stage: StageDto = {
    id: stageId,
    role: event.role,
    status: event.status,
    elapsedMs: typeof event.elapsedMs === 'number' && Number.isFinite(event.elapsedMs) ? event.elapsedMs : 0,
    inputTokens: typeof event.inputTokens === 'number' ? event.inputTokens : null,
    outputTokens: typeof event.outputTokens === 'number' ? event.outputTokens : null,
    toolCalls: typeof event.toolCalls === 'number' && Number.isFinite(event.toolCalls) ? event.toolCalls : 0,
    summary: strOrNull(event.summary),
    error: strOrNull(event.error),
  }
  const index = cycle.stages.findIndex((s) => s.id === stageId)
  if (index >= 0) cycle.stages.splice(index, 1, stage)
  else cycle.stages.push(stage)
}

function applyProposal(event: Extract<CycleEvent, { type: 'proposal' }>) {
  const cycle = latestCycle.value
  if (!cycle) return
  if (!isOneOf(event.action, PROPOSAL_ACTIONS)) return
  const id = typeof event.id === 'string' ? event.id : ''
  if (!id) return
  const proposal: ProposalDto = {
    id,
    action: event.action,
    symbol: strOrNull(event.symbol),
    side: event.side === 'long' || event.side === 'short' ? event.side : null,
    confidence: strOrNull(event.confidence),
    entryPrice: strOrNull(event.entryPrice),
    stopLossPrice: strOrNull(event.stopLossPrice),
    takeProfitPrice: strOrNull(event.takeProfitPrice),
    positionPct: strOrNull(event.positionPct),
    leverage: strOrNull(event.leverage),
    thesis: typeof event.thesis === 'string' ? event.thesis : '',
    invalidation: strOrNull(event.invalidation),
  }
  latestCycle.value = { ...cycle, proposal }
}

function applyVerdict(event: Extract<CycleEvent, { type: 'verdict' }>) {
  const cycle = latestCycle.value
  if (!cycle || event.cycleId !== cycle.cycle.id) return
  if (!isOneOf(event.decision, RISK_DECISIONS)) return
  const id = typeof event.id === 'string' ? event.id : ''
  if (!id) return
  const verdict: VerdictDto = {
    id,
    decision: event.decision,
    reasons: Array.isArray(event.reasons) ? event.reasons.filter((r): r is string => typeof r === 'string') : [],
    maxPositionPct: strOrNull(event.maxPositionPct),
    maxLeverage: strOrNull(event.maxLeverage),
    entryZoneMin: strOrNull(event.entryZoneMin),
    entryZoneMax: strOrNull(event.entryZoneMax),
    allowMarket: typeof event.allowMarket === 'boolean' ? event.allowMarket : null,
    validUntilUtc: strOrNull(event.validUntilUtc),
  }
  latestCycle.value = { ...cycle, verdict }
}

function applyOrder(event: Extract<CycleEvent, { type: 'order' }>) {
  const cycle = latestCycle.value
  if (!cycle) return
  if (typeof event.orderId !== 'number' || !Number.isFinite(event.orderId)) return
  if (event.cycleId !== cycle.cycle.id) return
  const order: CycleOrderDto = {
    orderId: event.orderId,
    cycleId: event.cycleId,
    traderId: typeof event.traderId === 'number' ? event.traderId : null,
    exchangeAccountId: typeof event.exchangeAccountId === 'number' ? event.exchangeAccountId : null,
    proposalId: strOrNull(event.proposalId),
    action: typeof event.action === 'string' ? event.action : 'unknown',
    side: strOrNull(event.side),
    symbol: strOrNull(event.symbol),
    quantity: strOrNull(event.quantity),
    filledQuantity: strOrNull(event.filledQuantity),
    price: strOrNull(event.price),
    averagePrice: strOrNull(event.averagePrice),
    status: isOneOf(event.status, ORDER_STATUSES) ? event.status : null,
    simulated: event.simulated === true,
    note: strOrNull(event.note),
    at: typeof event.at === 'string' ? event.at : new Date().toISOString(),
  }
  const orders = cycle.orders.filter((o) => o.orderId !== order.orderId)
  latestCycle.value = { ...cycle, orders: [...orders, order] }
  upsertRecentOrder(order)
}

function applyTool(event: Extract<CycleEvent, { type: 'tool' }>) {
  // cycleId 为 null = 复盘工具事件（不属于周期 UI）
  if (event.cycleId === null) return
  const cycle = latestCycle.value
  if (!cycle || event.cycleId !== cycle.cycle.id) return
  const stageId = typeof event.stageId === 'string' ? event.stageId : ''
  if (!stageId || typeof event.tool !== 'string' || !event.tool) return
  const at = typeof event.at === 'string' ? event.at : ''
  const elapsedMs = typeof event.elapsedMs === 'number' && Number.isFinite(event.elapsedMs) ? event.elapsedMs : 0
  const key = `${stageId}|${at}|${event.tool}|${elapsedMs}`
  const list = stageTools.value[stageId] ?? []
  if (list.some((chip) => chip.key === key)) return
  const chip: ToolChip = {
    key,
    stageId,
    tool: event.tool,
    success: event.success === true,
    error: strOrNull(event.error),
    errorCode: strOrNull(event.errorCode),
    elapsedMs,
    at,
  }
  stageTools.value = { ...stageTools.value, [stageId]: [...list, chip].slice(-30) }
}

function applyEquity(event: Extract<CycleEvent, { type: 'equity' }>) {
  if (!dashboard.value?.account) return
  if (typeof event.equity !== 'string' || event.equity === '') return
  const account = dashboard.value.account
  account.equity = event.equity
  // 总盈亏/收益率是从净值推导的展示值：净值更新后同步重算，避免统计卡互相打架
  const equity = toNumber(event.equity)
  const initial = toNumber(dashboard.value.trader.initialBalance)
  if (equity !== null && initial !== null && initial > 0) {
    const pnl = equity - initial
    account.totalPnl = pnl.toFixed(2)
    account.pnlPct = ((pnl / initial) * 100).toFixed(2)
  } else {
    account.totalPnl = null
    account.pnlPct = null
  }
  if (typeof event.at === 'string' && event.at) {
    account.at = event.at
    appendSample(event.at, event.equity)
  }
}

// position-history 帧只带单行（symbol/realizedPnl/closeTime），聚合指标仍由后端计算：
// 先按 symbol+closeTime 就地更新对应行，再节流重拉 dashboard 让历史/指标按服务端口径刷新。
function applyPositionHistory(event: Extract<CycleEvent, { type: 'position-history' }>) {
  if (!dashboard.value) return
  if (typeof event.symbol === 'string' && typeof event.closeTime === 'string' && typeof event.realizedPnl === 'string') {
    const row = dashboard.value.history.rows.find(
      (r) => r.symbol === event.symbol && r.closeTime === event.closeTime,
    )
    if (row) row.realizedPnl = event.realizedPnl
  }
  const now = Date.now()
  if (now - lastPositionHistoryRefreshAt < 3000) return
  lastPositionHistoryRefreshAt = now
  void loadDashboard(true)
}

// 净值样本点追加（字符串金额原样保存；时间升序，保留最近 200 个）
function appendSample(at: string, equity: string) {
  if (!dashboard.value || !at || !equity) return
  const samples = dashboard.value.samples
  if (samples.length > 0 && samples[samples.length - 1].at >= at) return
  dashboard.value.samples = [...samples, { at, equity }].slice(-200)
}

// 执行审计：新订单插入列表头（按 Id 去重，保留 20 条）。
// recentOrders 是旧链路 TraderOrderDto：只有 id/quantity/price/note/error，没有 status/simulated/success。
function upsertRecentOrder(order: CycleOrderDto) {
  if (!dashboard.value || typeof order.orderId !== 'number') return
  // SSE order 载荷没有 error 字段：已有同 id 行时保留其错误文案，避免更新时被清空
  const existing = dashboard.value.recentOrders.find((o) => o.id === order.orderId)
  const row: RecentOrderDto = {
    id: order.orderId,
    at: order.at,
    symbol: order.symbol,
    action: order.action,
    quantity: order.quantity,
    price: order.price,
    note: order.note,
    error: existing?.error ?? null,
  }
  const rest = dashboard.value.recentOrders.filter((o) => o.id !== row.id)
  dashboard.value.recentOrders = [row, ...rest].sort((a, b) => b.id - a.id).slice(0, 20)
}

// 净值曲线/初始余额：图组件需要数字，这里只在绘图边界解析字符串并守卫有限性。
const chartSamples = computed<DashboardSample[]>(() => {
  const samples: DashboardSample[] = []
  for (const sample of dashboard.value?.samples ?? []) {
    const equity = toNumber(sample.equity)
    if (equity !== null) samples.push({ at: sample.at, equity })
  }
  return samples
})
const chartInitialBalance = computed(() => toNumber(dashboard.value?.trader.initialBalance))

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

// 品种表现：直接使用后端聚合的 history.symbols（金额/胜率为字符串），只做筛选与分页
const symbolPageSize = ref(HISTORY_PAGE_SIZE_OPTIONS[0])
const symbolPage = ref(1)
const symbolFilterSymbol = ref<string | undefined>(undefined)

const symbolStats = computed<DashboardSymbolStatDto[]>(() => dashboard.value?.history.symbols ?? [])
const filteredSymbolStats = computed(() =>
  symbolStats.value
    .filter((s) => !symbolFilterSymbol.value || s.symbol === symbolFilterSymbol.value)
    .sort((a, b) => b.count - a.count || a.symbol.localeCompare(b.symbol)),
)

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
  winRate: string | null
  totalPnl: string
  avgPnl: string | null
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
      totalPnl: found?.totalPnl ?? '0',
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

// 快照与 SSE 增量合并：同周期 id 时按 id 联合，事件侧优先（查询可能早于在途事件）。
function mergeCycleDetail(base: CycleDetailDto, live: CycleDetailDto | null): CycleDetailDto {
  if (!live || live.cycle.id !== base.cycle.id) return base
  const stages = new Map(base.stages.map((s) => [s.id, s] as const))
  for (const s of live.stages) stages.set(s.id, s)
  const orders = new Map(base.orders.map((o) => [o.orderId, o] as const))
  for (const o of live.orders) orders.set(o.orderId, o)
  const cycle = live.cycle.status !== 'running' && base.cycle.status === 'running' ? live.cycle : base.cycle
  return {
    cycle,
    stages: [...stages.values()],
    proposal: live.proposal ?? base.proposal,
    verdict: live.verdict ?? base.verdict,
    orders: [...orders.values()].sort((a, b) => a.orderId - b.orderId),
  }
}

function cycleStartedMs(cycle: CycleDto): number {
  const ms = new Date(cycle.startedAt).getTime()
  return Number.isFinite(ms) ? ms : 0
}

/** 快照与实时周期择新：同 id 合并；快照缺失或更旧时保留实时周期（SSE 已看到的新周期不能被旧快照回退）。 */
function pickSnapshotCycle(snapshot: CycleDetailDto | null, live: CycleDetailDto | null): CycleDetailDto | null {
  if (!snapshot) return live
  if (!live) return snapshot
  if (snapshot.cycle.id === live.cycle.id) return mergeCycleDetail(snapshot, live)
  return cycleStartedMs(live.cycle) > cycleStartedMs(snapshot.cycle) ? live : snapshot
}

async function loadDashboard(silent = false): Promise<boolean> {
  const id = selectedId.value
  const generation = dashboardGeneration
  if (id === null) return false
  if (!silent) loading.value = true
  try {
    const fetched = await fetchTraderDashboard(id)
    if (selectedId.value !== id || generation !== dashboardGeneration) return false
    const liveCycle = latestCycle.value
    const nextCycle = pickSnapshotCycle(fetched.latestCycle, liveCycle)
    const nextCycleId = nextCycle?.cycle.id ?? null
    if ((liveCycle?.cycle.id ?? null) !== nextCycleId) stageTools.value = {}
    // SSE 净值帧可能比在途快照新：按采样时间保留较新的账户块
    const liveAccount = dashboard.value?.account
    if (fetched.account && liveAccount && liveAccount.at > fetched.account.at) {
      fetched.account = liveAccount
    }
    dashboard.value = fetched
    latestCycle.value = nextCycle
    cycleErrorText.value = null
    errorText.value = null
    return true
  } catch (err) {
    if (!silent && generation === dashboardGeneration && selectedId.value === id) {
      errorText.value = err instanceof Error ? err.message : '加载失败'
    }
    return false
  } finally {
    if (!silent && selectedId.value === id) loading.value = false
  }
}

// 周期详情（阶段/提案/裁决/订单）：仅新周期 id 变化时按需拉取；force 用于重连重同步。
let cycleRequestSeq = 0
async function loadCycleDetail(cycleId: string, force = false) {
  if (!force && latestCycle.value?.cycle.id === cycleId) return
  const traderId = selectedId.value
  const generation = dashboardGeneration
  const seq = ++cycleRequestSeq
  cycleLoading.value = true
  try {
    const detail = await getCycle(cycleId)
    if (seq !== cycleRequestSeq) return
    // 请求发出后切换了交易员：旧交易员的详情不得填充新看板
    if (generation !== dashboardGeneration || selectedId.value !== traderId) return
    if (traderId !== null && detail.cycle.traderId !== null && detail.cycle.traderId !== traderId) return
    const live = latestCycle.value
    if (live && live.cycle.id !== cycleId) return
    latestCycle.value = mergeCycleDetail(detail, live)
    cycleErrorText.value = null
  } catch (err) {
    if (seq === cycleRequestSeq && generation === dashboardGeneration && selectedId.value === traderId) {
      cycleErrorText.value = err instanceof Error ? err.message : '周期详情加载失败'
    }
  } finally {
    if (seq === cycleRequestSeq) cycleLoading.value = false
  }
}

let retryTimer: number | undefined

// 首屏加载：失败（后端未就绪等）时 5 秒后重试直到成功——成功即停，不是轮询
async function bootstrap() {
  try {
    await loadTraders()
    // 零交易员：页面展示空态即可，不进入 5 秒重试循环
    if (traders.value.length === 0) {
      errorText.value = null
      if (retryTimer !== undefined) {
        window.clearTimeout(retryTimer)
        retryTimer = undefined
      }
      return
    }
    const ok = await loadDashboard()
    if (!ok) throw new Error(errorText.value ?? '加载失败')
    if (selectedId.value !== null) connectStream(selectedId.value)
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
  // 存活看门狗：服务端每 15 秒发 ping，45 秒无任何帧才提示；不主动重启连接
  livenessTimer = window.setInterval(() => {
    const active = streamStatus.value === 'open' || streamStatus.value === 'reconnecting'
    streamStale.value = active && lastFrameAt > 0 && Date.now() - lastFrameAt > 45000
  }, 10000)
})

onUnmounted(() => {
  if (retryTimer !== undefined) window.clearTimeout(retryTimer)
  if (clockTimer !== undefined) window.clearInterval(clockTimer)
  if (livenessTimer !== undefined) window.clearInterval(livenessTimer)
  disconnectStream()
})

watch(selectedId, async (id) => {
  dashboardGeneration += 1
  const generation = dashboardGeneration
  historyPage.value = 1
  symbolPage.value = 1
  latestCycle.value = null
  cycleErrorText.value = null
  stageTools.value = {}
  malformedFrameCount.value = 0
  streamStatus.value = 'closed'
  disposeCurrentStream()
  streamHasOpened = false
  needsResync = false
  closedRebuildUsed = false
  if (closedRebuildTimer !== undefined) {
    window.clearTimeout(closedRebuildTimer)
    closedRebuildTimer = undefined
  }
  pendingEvents.length = 0
  if (id === null) {
    disconnectStream()
    return
  }
  await loadDashboard()
  if (generation !== dashboardGeneration || selectedId.value !== id) return
  connectStream(id)
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
  open: { label: '开仓', color: 'arcoblue', icon: '▶' },
  buy: { label: '开多', color: 'green', icon: '↑' },
  sell: { label: '开空', color: 'red', icon: '↓' },
  close: { label: '平仓', color: 'arcoblue', icon: '⇄' },
  skip: { label: '跳过', color: 'gray', icon: '⏭' },
  error: { label: '轮次失败', color: 'red', icon: '!' },
  hold: { label: '观望', color: 'gray', icon: '⏸' },
}

function actionInfo(action: string | null) {
  return (action && ACTION_MAP[action]) || { label: action ?? '—', color: 'gray', icon: '·' }
}

function sideLabel(side: string | null | undefined): string {
  if (!side) return '—'
  if (side.toLowerCase().includes('long') || side === 'Buy') return '多头'
  if (side.toLowerCase().includes('short') || side === 'Sell') return '空头'
  return side
}

// 决策卡止损/止盈幅度：后端字符串 → 解析 → 绝对值 + 2 位小数（纯展示）
function fmtTime(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString('zh-CN', { hour12: false })
}

function fmtClock(iso: string | null): string {
  if (!iso) return '--:--:--'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '--:--:--' : d.toLocaleTimeString('zh-CN', { hour12: false })
}

const EXCHANGE_LABELS: Record<string, string> = {
  binance: 'BINANCE',
  bybit: 'BYBIT',
  okx: 'OKX',
  bitget: 'BITGET',
  gateio: 'GATE',
  kucoin: 'KUCOIN',
}

// 后端 exchangeLabel 取枚举名（GateIo）；直接大写会得到 GATEIO，这里按交易所惯用简称展示
const exchangeLabel = computed(() => {
  const raw = dashboard.value?.trader.exchangeLabel ?? ''
  return EXCHANGE_LABELS[raw.toLowerCase()] ?? raw.toUpperCase()
})

const runtimeText = computed(() => {
  const info = dashboard.value?.trader
  if (!info?.isRunning || !info.startedAt) return info?.isRunning ? '运行中' : '已停止'
  const minutes = Math.max(0, Math.floor((clock.value - new Date(info.startedAt).getTime()) / 60000))
  return minutes >= 60 ? `${Math.floor(minutes / 60)} 小时 ${minutes % 60} 分钟` : `${minutes} 分钟`
})

const historyMetrics = computed(() => {
  const m = dashboard.value?.history.metrics
  return m
    ? [
        { label: '总交易次数', value: String(m.totalTrades) },
        { label: '胜率', value: fmtPct(m.winRate, 1) },
        { label: '总盈亏', value: signedAmount(m.totalPnl), cls: pnlClass(m.totalPnl) },
        { label: '净盈亏', value: signedAmount(m.netPnl), cls: pnlClass(m.netPnl), hint: '交易所共享接口未提供手续费字段' },
        { label: '盈利因子', value: fmtNumber(m.profitFactor, 2) },
        { label: '盈亏比', value: ratioOf(m.avgWin, m.avgLoss) },
        { label: '平均盈利', value: `$${fmtAmount(m.avgWin)}`, cls: 'gain' },
        { label: '平均亏损', value: `$${fmtAmount(m.avgLoss)}`, cls: 'loss' },
        { label: '最大回撤', value: fmtPct(m.maxDrawdownPct, 1), cls: 'loss' },
        { label: '夏普比率', value: fmtNumber(m.sharpe, 2), hint: '近似口径：按笔盈亏均值/标准差×√笔数' },
      ]
    : []
})

// 启停/平仓错误：422 等业务错误原样展示后端文案；409 cycle_in_progress 是并发提示，用警告而非错误弹层
function showActionError(err: unknown, fallback: string) {
  if (err instanceof ApiRequestError) {
    if (err.code === 'cycle_in_progress') {
      Message.warning(err.message)
      return
    }
    Message.error(err.message)
    return
  }
  Message.error(err instanceof Error ? err.message : fallback)
}

async function onStartTrader() {
  const id = selectedId.value
  if (id === null || actionBusy.value) return
  actionBusy.value = true
  try {
    await startTrader(id)
    Message.success('交易员已启动')
    await loadDashboard(true)
  } catch (err) {
    showActionError(err, '启动失败')
  } finally {
    actionBusy.value = false
  }
}

async function onStopTrader() {
  const id = selectedId.value
  if (id === null || actionBusy.value) return
  actionBusy.value = true
  try {
    await stopTrader(id)
    Message.success('交易员已停止')
    await loadDashboard(true)
  } catch (err) {
    showActionError(err, '停止失败')
  } finally {
    actionBusy.value = false
  }
}

const ORDER_STATUS_LABELS: Record<OrderStatus, string> = {
  submitted: '已提交',
  filled: '已成交',
  partial: '部分成交',
  failed: '失败',
  unknown: '结果未知',
  cancelled: '已撤销',
}

function orderStatusLabel(status: OrderStatus | null): string {
  return status ? (ORDER_STATUS_LABELS[status] ?? status) : '状态未知'
}

/** 平仓订单未成交时补齐周期级 errorCode/error（手动平仓响应只在 order=null 时带回它们）。 */
async function loadCloseFailure(cycleId: string): Promise<{ code: string | null; error: string | null }> {
  try {
    const detail = await getCycle(cycleId)
    return { code: detail.cycle.errorCode, error: detail.cycle.error }
  } catch {
    return { code: null, error: null }
  }
}

// 手动平仓（新链路）：quantity/price 为字符串金额；只有 order.status=filled 才算平仓成功。
async function closePosition(symbol: string) {
  const id = selectedId.value
  if (id === null || closingSymbol.value) return
  closingSymbol.value = symbol
  try {
    const result = await apiClosePosition(id, { symbol, orderType: 'market', reason: '手动平仓' })
    const order = result.order
    if (!order) {
      Message.warning(result.error ?? result.errorCode ?? '平仓未执行（后端未返回订单）')
      // 平仓未执行但周期已留痕：静默刷新让最新周期块如实展示失败原因（不报成功）
      await loadDashboard(true)
      return
    }
    if (order.status === 'filled') {
      Message.success(`已平仓 ${symbol}（订单 #${order.orderId}）`)
    } else if (order.status === 'submitted' || order.status === 'partial') {
      Message.info(`已提交，等待成交（订单 #${order.orderId}）`)
    } else {
      let code = result.errorCode
      let error = result.error
      if (!code && !error) {
        const cycleFailure = await loadCloseFailure(result.cycleId)
        code = cycleFailure.code
        error = cycleFailure.error
      }
      const detail = [code, error, order.note].filter((part): part is string => typeof part === 'string' && part.trim() !== '')
      Message.warning(
        `平仓未完成（${orderStatusLabel(order.status)}）${detail.length > 0 ? `：${detail.join('；')}` : ''}`,
      )
    }
    await loadDashboard(true)
  } catch (err) {
    showActionError(err, '平仓失败')
  } finally {
    closingSymbol.value = null
  }
}

// 工具调用 chips：按 stageId 分组；阶段行未落库前用 stageId 前缀兜底显示
const ROLE_LABELS: Record<StageRole, string> = {
  analyst: '分析师',
  trader: '交易员',
  risk: '风控官',
  executor: '执行员',
  reviewer: '复盘员',
}

const toolGroups = computed(() => {
  const stages = latestCycle.value?.stages ?? []
  return Object.entries(stageTools.value)
    .map(([stageId, tools]) => {
      const stage = stages.find((s) => s.id === stageId)
      return { stageId, label: stage ? ROLE_LABELS[stage.role] : `阶段 ${stageId.slice(0, 8)}`, tools }
    })
    .filter((group) => group.tools.length > 0)
})

function shortCycleId(id: string): string {
  return id.slice(0, 8)
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
          <a-button
            v-if="!dashboard.trader.isRunning"
            type="primary"
            size="small"
            :loading="actionBusy"
            @click="onStartTrader"
          >启动</a-button>
          <a-button
            v-else
            status="danger"
            size="small"
            :loading="actionBusy"
            @click="onStopTrader"
          >停止</a-button>
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
        <span>{{ exchangeLabel }} - {{ dashboard.trader.accountName }}</span>
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
        <a-tag size="small" :color="streamStatusMeta.color">推送{{ streamStatusMeta.label }}</a-tag>
        <a-tooltip v-if="!dashboard.trader.capabilityCanTrade && dashboard.trader.executionMode === 'live'" :content="dashboard.trader.capabilityNotes.join('；')">
          <a-tag size="small" color="orange">实盘能力不足</a-tag>
        </a-tooltip>
        </div>
      </div>

      <!-- 调试栏（含 SSE 连接状态） -->
      <div class="debug-bar">
        <span class="debug-left">
          系统状态::{{ dashboard.trader.isRunning ? 'ONLINE' : 'STOPPED' }}
          <span class="stream-state" :class="`stream-${streamStatus}`">{{ streamStatusMeta.label }}</span>
        </span>
        <span class="debug-right">
          最后更新::{{ fmtClock(dashboard.account?.at ?? dashboard.trader.lastCycleAt) }}
          <template v-if="dashboard.account?.success">
            · 净值::{{ fmtAmount(dashboard.account?.equity) }}
            · 盈亏::{{ signedAmount(dashboard.account?.totalPnl) }}
          </template>
        </span>
      </div>

      <a-alert v-if="streamStale" type="warning" class="page-alert">
        连接可能已断开：超过 45 秒未收到任何推送帧，正在等待自动重连…
      </a-alert>
      <a-alert v-if="malformedFrameCount > 0" type="warning" class="page-alert">
        已跳过 {{ malformedFrameCount }} 个无法解析的推送帧（不影响后续事件）。
      </a-alert>

      <a-alert v-if="dashboard.account && !dashboard.account.success" type="warning" class="page-alert">
        账户数据读取失败：{{ dashboard.account.error }}（请检查 API Key 权限/IP 白名单；下方账户指标暂不可用）
      </a-alert>

      <!-- 统计卡 -->
      <div class="stats-row">
        <div class="stat-card">
          <div class="stat-title">总净值</div>
          <div class="stat-value" :class="pnlClass(dashboard.account?.totalPnl)">
            {{ fmtAmount(dashboard.account?.equity) }}<span v-if="dashboard.account?.equity !== null && dashboard.account?.equity !== undefined" class="stat-unit">USDT</span>
          </div>
          <div v-if="dashboard.account?.pnlPct !== null && dashboard.account?.pnlPct !== undefined" class="stat-sub" :class="pnlClass(dashboard.account?.totalPnl)">
            {{ signedPct(dashboard.account?.pnlPct, 2) }}
          </div>
          <div v-else class="stat-sub">{{ dashboard.account?.success ? '相对初始余额' : '账户读取失败' }}</div>
        </div>
        <div class="stat-card">
          <div class="stat-title">可用余额</div>
          <div class="stat-value">
            {{ fmtAmount(dashboard.account?.available) }}<span v-if="dashboard.account?.available !== null && dashboard.account?.available !== undefined" class="stat-unit">USDT</span>
          </div>
          <div v-if="dashboard.account?.available !== null && dashboard.account?.available !== undefined && dashboard.account?.equity" class="stat-sub">
            {{ ratioPct(dashboard.account.available, dashboard.account.equity) }} 空闲
          </div>
        </div>
        <div class="stat-card">
          <div class="stat-title">总盈亏</div>
          <div class="stat-value" :class="pnlClass(dashboard.account?.totalPnl)">
            {{ signedAmount(dashboard.account?.totalPnl) }}<span v-if="dashboard.account?.totalPnl !== null && dashboard.account?.totalPnl !== undefined" class="stat-unit">USDT</span>
          </div>
          <div v-if="dashboard.trader.initialBalance !== null" class="stat-sub">初始余额 {{ fmtAmount(dashboard.trader.initialBalance) }}</div>
          <div v-else class="stat-sub">未设置初始余额</div>
        </div>
        <div class="stat-card">
          <div class="stat-title">持仓</div>
          <div class="stat-value">{{ dashboard.positions.length }}<span class="stat-unit">ACTIVE</span></div>
          <div v-if="dashboard.account?.marginUsedPct !== null && dashboard.account?.marginUsedPct !== undefined" class="stat-sub">
            保证金: {{ fmtPct(dashboard.account.marginUsedPct, 1) }}
          </div>
        </div>
      </div>

      <!-- 最新周期（新链路：周期/阶段/提案/裁决/订单 + SSE 增量） -->
      <div class="cycle-section">
        <div class="cycle-section-head">
          <span class="cycle-section-title">最新周期</span>
          <a-space :size="6">
            <a-tag v-if="cycleLoading" size="small" color="arcoblue">详情加载中</a-tag>
            <a-tag v-if="latestCycle" size="small" color="purple">#{{ shortCycleId(latestCycle.cycle.id) }}</a-tag>
          </a-space>
        </div>
        <a-alert v-if="cycleErrorText" type="warning" class="page-alert" closable @close="cycleErrorText = null">
          {{ cycleErrorText }}
        </a-alert>
        <div v-if="!latestCycle" class="cycle-empty">暂无周期记录（交易员启动后按计划生成周期）</div>
        <template v-else>
          <CycleTimeline :cycle="latestCycle.cycle" :stages="latestCycle.stages" :proposal="latestCycle.proposal" />
          <div v-if="toolGroups.length > 0" class="tool-panel">
            <div class="tool-panel-title">工具调用</div>
            <div v-for="group in toolGroups" :key="group.stageId" class="tool-group">
              <span class="tool-group-label">{{ group.label }}</span>
              <a-tag
                v-for="chip in group.tools"
                :key="chip.key"
                size="small"
                :color="chip.success ? 'green' : 'red'"
                class="tool-chip"
              >{{ chip.tool }}<template v-if="!chip.success && chip.error"> · {{ chip.error }}</template></a-tag>
            </div>
          </div>
          <div v-if="latestCycle.proposal || latestCycle.verdict || latestCycle.orders.length > 0" class="cycle-cards">
            <ProposalCard v-if="latestCycle.proposal" :proposal="latestCycle.proposal" />
            <VerdictCard v-if="latestCycle.verdict" :verdict="latestCycle.verdict" />
            <OrderCard v-for="order in latestCycle.orders" :key="order.orderId" :order="order" />
          </div>
        </template>
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
                <span class="chart-equity">{{ fmtAmount(dashboard.account?.equity) }} <span class="stat-unit">USDT</span></span>
                <span v-if="dashboard.account?.totalPnl !== null && dashboard.account?.totalPnl !== undefined" class="chart-pnl" :class="pnlClass(dashboard.account?.totalPnl)">
                  {{ signedAmount(dashboard.account?.totalPnl) }} USDT
                </span>
              </template>
              <span v-else class="chart-unavailable">账户数据不可用——修复 API Key 权限 / IP 白名单后自动展示净值曲线</span>
            </div>
            <EquityChart
              :samples="chartSamples"
              :initial-balance="chartInitialBalance"
              :percent-mode="percentMode"
            />
            <div class="chart-footer">
              <span>初始余额：{{ fmtAmount(dashboard.trader.initialBalance) }}</span>
              <span>当前净值：{{ fmtAmount(dashboard.account?.equity) }}</span>
              <span>净值采样：{{ dashboard.samples.length }} 个</span>
              <span>显示范围：{{ percentMode ? '%' : 'USDT' }}</span>
            </div>
          </a-card>

          <!-- 当前持仓 -->
          <a-card class="positions-card">
            <template #title>当前持仓</template>
            <template #extra>
              <a-space :size="6">
                <a-tag :color="streamStatusMeta.color" size="small">{{ streamStatusMeta.label }}</a-tag>
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
                  <th class="num">均价</th>
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
                  <td class="num">{{ text(p.quantity) }}</td>
                  <td class="num">{{ p.leverage ? `${p.leverage}x` : '—' }}</td>
                  <td class="num">{{ text(p.averageOpenPrice) }}</td>
                  <td class="num" :class="pnlClass(p.unrealizedPnl)">{{ signedAmount(p.unrealizedPnl) }}</td>
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
                  {{ dir.empty ? '—' : signedAmount(dir.totalPnl) }}
                </span>
              </div>
              <div class="direction-stats">
                <span>交易次数 {{ dir.count }}</span>
                <span :class="dir.empty ? '' : 'gain'">
                  胜率 {{ dir.winRate === null ? '—' : fmtPct(dir.winRate, 0) }}
                </span>
                <span :class="dir.empty ? '' : pnlClass(dir.avgPnl)">
                  平均盈亏 {{ dir.avgPnl === null ? '—' : signedAmount(dir.avgPnl) }}
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
                  <td class="num gain">{{ fmtPct(s.winRate, 0) }}</td>
                  <td class="num" :class="pnlClass(s.totalPnl)">{{ signedAmount(s.totalPnl) }}</td>
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
                  <td class="num">{{ text(r.quantity) }}</td>
                  <td class="num">{{ r.leverage ? `${r.leverage}x` : '—' }}</td>
                  <td class="num">{{ text(r.entryPrice) }}</td>
                  <td class="num">{{ text(r.exitPrice) }}</td>
                  <td class="num" :class="pnlClass(r.realizedPnl)">{{ signedAmount(r.realizedPnl) }}</td>
                  <td class="num fee-cell">{{ r.fee !== null ? fmtAmount(r.fee, 4) : '—' }}</td>
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
              <tr><th>时间</th><th>标的</th><th>动作</th><th class="num">数量</th><th class="num">价格</th><th>说明</th></tr>
            </thead>
            <tbody>
              <tr v-for="o in dashboard.recentOrders" :key="o.id">
                <td>{{ fmtTime(o.at) }}</td>
                <td>{{ o.symbol ?? '—' }}</td>
                <td>{{ actionInfo(o.action).label }}</td>
                <td class="num">{{ text(o.quantity) }}</td>
                <td class="num">{{ text(o.price) }}</td>
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

.debug-left {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

/* SSE 连接状态（文案与 onStatus 一一对应：连接中/已连接/重连中/连接失败） */
.stream-state {
  padding: 0 6px;
  border-radius: 3px;
  font-size: 10px;
  line-height: 16px;
  border: 1px solid var(--color-border-2);
}

.stream-open {
  color: rgb(var(--success-6));
  border-color: rgba(var(--success-6), 0.4);
}

.stream-connecting {
  color: rgb(var(--primary-6));
  border-color: rgba(var(--primary-6), 0.4);
}

.stream-reconnecting {
  color: rgb(var(--warning-6));
  border-color: rgba(var(--warning-6), 0.4);
}

.stream-error,
.stream-closed {
  color: rgb(var(--danger-6));
  border-color: rgba(var(--danger-6), 0.4);
}

.debug-right {
  display: flex;
  gap: 16px;
}

/* 最新周期（新链路） */
.cycle-section {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.cycle-section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.cycle-section-title {
  font-size: 15px;
  font-weight: 600;
  color: var(--color-text-1);
}

.cycle-empty {
  padding: 24px 0;
  text-align: center;
  font-size: 13px;
  color: var(--color-text-3);
  border: 1px dashed var(--color-border-2);
  border-radius: 8px;
  background: var(--color-fill-1);
}

.tool-panel {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid var(--color-border-1);
  border-radius: 8px;
  background: var(--color-fill-1);
}

.tool-panel-title {
  font-size: 11px;
  letter-spacing: 0.08em;
  color: var(--color-text-3);
}

.tool-group {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  flex-wrap: wrap;
}

.tool-group-label {
  min-width: 48px;
  font-size: 12px;
  line-height: 22px;
  color: var(--color-text-2);
}

.tool-chip {
  height: auto;
  max-width: 100%;
  line-height: 18px;
  white-space: normal;
  word-break: break-word;
}

.cycle-cards {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 12px;
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

/* 只有左列内容（净值曲线 + 当前持仓）：单列全宽，避免右侧 1fr 留白 */
.main-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 16px;
  align-items: stretch;
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
