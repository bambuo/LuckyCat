import type {
  ApiResponse,
  CycleDetailDto,
  CycleDto,
  CycleEvent,
  ExchangeAccount,
  ExchangeBalanceDto,
  ExchangeUpsertRequest,
  ExchangeVerifyEvent,
  HealthDto,
  LessonDto,
  ManualCloseRequest,
  ManualCloseResultDto,
  OutboundIp,
  PagedDto,
  PromptPreview,
  PromptPreviewResult,
  Provider,
  ProviderUpsertRequest,
  ReviewListItemDto,
  ReviewRunDto,
  StageDetailDto,
  Strategy,
  StrategyConfig,
  StrategyDefaults,
  StrategyTestResult,
  StrategyUpsertRequest,
  StrategyValidationResult,
  TestRunRequest,
  Trader,
  TraderDashboard,
  TraderDashboardDto,
  TraderUpsertRequest,
  VerifyEvent,
} from './types'

/** 请求失败：code 来自包裹 error.code，status 保留 HTTP 状态码（无包裹时也可用于 401/409/422 分支）。 */
export class ApiRequestError extends Error {
  readonly code: string | null
  readonly status: number | null

  constructor(message: string, code: string | null, status: number | null) {
    super(message)
    this.name = 'ApiRequestError'
    this.code = code
    this.status = status
  }
}

/** 解析统一包裹；非 JSON / 非对象 / 无 success 字段一律视为无包裹（调用方用固定文案兜底）。 */
function parseApiEnvelope<T>(text: string): ApiResponse<T> | null {
  if (!text.trim()) return null
  try {
    const value: unknown = JSON.parse(text)
    if (
      value !== null &&
      typeof value === 'object' &&
      typeof (value as { success?: unknown }).success === 'boolean'
    ) {
      return value as ApiResponse<T>
    }
  } catch {
    // 网关错误页等非 JSON 响应：不向上暴露原文
  }
  return null
}

/** 有请求体才带 Content-Type；无体 GET 不加无谓的头。 */
function jsonHeaders(init?: RequestInit): Headers {
  const headers = new Headers(init?.headers)
  const hasBody = init?.body !== undefined && init?.body !== null
  if (hasBody && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  return headers
}

/** 把失败响应归一为 ApiRequestError：有包裹用包裹文案/码，否则用含状态码的固定文案（绝不回显原文）。 */
function apiError(status: number, text: string): ApiRequestError {
  const envelope = parseApiEnvelope<unknown>(text)
  return new ApiRequestError(
    envelope?.error?.message ?? `请求失败（HTTP ${status}）`,
    envelope?.error?.code ?? null,
    status,
  )
}

/**
 * 统一请求：先读文本再尝试 JSON（204 空体、网关非 JSON 都不会抛 SyntaxError）；
 * 非 2xx 或 success=false 抛 ApiRequestError（带 code/status）；不打印、不透传原始响应文本。
 */
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, { ...init, headers: jsonHeaders(init) })
  const text = await response.text().catch(() => '')

  if (!response.ok) throw apiError(response.status, text)

  const envelope = parseApiEnvelope<T>(text)
  if (!envelope) {
    // 204 / 空体：调用方按 void 处理；非空却不可解析说明网关改写了响应。
    if (!text.trim()) return undefined as T
    throw new ApiRequestError(`响应格式异常（HTTP ${response.status}）`, null, response.status)
  }
  if (!envelope.success) {
    throw new ApiRequestError(
      envelope.error?.message ?? `请求失败（HTTP ${response.status}）`,
      envelope.error?.code ?? null,
      response.status,
    )
  }
  return envelope.data as T
}

// —— 系统 ——

export const getHealth = () => request<HealthDto>('/api/health')

// —— 提供商 ——

export const listProviders = () => request<Provider[]>('/api/providers')

export const createProvider = (data: ProviderUpsertRequest) =>
  request<Provider>('/api/providers', { method: 'POST', body: JSON.stringify(data) })

export const updateProvider = (id: number, data: ProviderUpsertRequest) =>
  request<Provider>(`/api/providers/${id}`, { method: 'PUT', body: JSON.stringify(data) })

export const deleteProvider = (id: number) =>
  request<void>(`/api/providers/${id}`, { method: 'DELETE' })

// —— 交易所 ——

export const listExchanges = () => request<ExchangeAccount[]>('/api/exchanges')

export const createExchange = (data: ExchangeUpsertRequest) =>
  request<ExchangeAccount>('/api/exchanges', { method: 'POST', body: JSON.stringify(data) })

export const updateExchange = (id: number, data: ExchangeUpsertRequest) =>
  request<ExchangeAccount>(`/api/exchanges/${id}`, { method: 'PUT', body: JSON.stringify(data) })

export const deleteExchange = (id: number) =>
  request<void>(`/api/exchanges/${id}`, { method: 'DELETE' })

export const getOutboundIp = () => request<OutboundIp>('/api/exchanges/outbound-ip')

/** 真实读取账户 USDT 余额；新链路金额为字符串（旧 getExchangeBalance 保留给当前表单）。 */
export const fetchExchangeBalance = (id: number) =>
  request<ExchangeBalanceDto>(`/api/exchanges/${id}/balance`)

async function streamSse<T>(
  url: string,
  onEvent: (event: T) => void,
  signal?: AbortSignal,
): Promise<void> {
  const res = await fetch(url, { method: 'POST', signal })
  if (!res.ok || !res.body) throw new Error(`验证请求失败（HTTP ${res.status}）`)

  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })

    let index: number
    while ((index = buffer.indexOf('\n\n')) >= 0) {
      const chunk = buffer.slice(0, index)
      buffer = buffer.slice(index + 2)
      const dataLine = chunk.split('\n').find((line) => line.startsWith('data:'))
      if (!dataLine) continue
      onEvent(JSON.parse(dataLine.slice(5).trim()) as T)
    }
  }
}

export function verifyProvider(
  id: number,
  onEvent: (event: VerifyEvent) => void,
  signal?: AbortSignal,
): Promise<void> {
  return streamSse(`/api/providers/${id}/verify`, onEvent, signal)
}

export function verifyExchange(
  id: number,
  onEvent: (event: ExchangeVerifyEvent) => void,
  signal?: AbortSignal,
): Promise<void> {
  return streamSse(`/api/exchanges/${id}/verify`, onEvent, signal)
}

// —— 策略 ——

export const listStrategies = () => request<Strategy[]>('/api/strategies')

export const createStrategy = (data: StrategyUpsertRequest) =>
  request<Strategy>('/api/strategies', { method: 'POST', body: JSON.stringify(data) })

export const updateStrategy = (id: number, data: StrategyUpsertRequest) =>
  request<Strategy>(`/api/strategies/${id}`, { method: 'PUT', body: JSON.stringify(data) })

export const deleteStrategy = (id: number) =>
  request<void>(`/api/strategies/${id}`, { method: 'DELETE' })

export const activateStrategy = (id: number) =>
  request<void>(`/api/strategies/${id}/activate`, { method: 'POST' })

export const duplicateStrategy = (id: number) =>
  request<Strategy>(`/api/strategies/${id}/duplicate`, { method: 'POST' })

export const getStrategyDefaults = () => request<StrategyDefaults>('/api/strategies/default-config')

/** 旧单角色预览：后端 /preview-prompt（单数）已删除，保留导出供当前页面编译；新页面用 previewStrategyPrompts。 */
export const previewStrategyPrompt = (config: StrategyConfig) =>
  request<PromptPreview>('/api/strategies/preview-prompt', {
    method: 'POST',
    body: JSON.stringify({ config }),
  })

/** preview-prompts：五角色系统提示词预览（不调用模型、不落库）。 */
export const previewStrategyPrompts = (config: StrategyConfig) =>
  request<PromptPreviewResult>('/api/strategies/preview-prompts', {
    method: 'POST',
    body: JSON.stringify({ config }),
  })

/** validate：通过返回 {valid,errors}；不通过抛 ApiRequestError（code=validation_failed，HTTP 400）。 */
export const validateStrategy = (config: StrategyConfig) =>
  request<StrategyValidationResult>('/api/strategies/validate', {
    method: 'POST',
    body: JSON.stringify({ config }),
  })

/** 旧 JSON 演练：/test-run 已改为 SSE，保留导出供当前页面编译；新页面用 openTestRunStream。 */
export const runStrategyTest = (config: StrategyConfig, providerId: number, strategyId: number | null) =>
  request<StrategyTestResult>('/api/strategies/test-run', {
    method: 'POST',
    body: JSON.stringify({ config, providerId, strategyId }),
  })

// —— 交易员 ——

export const listTraders = () => request<Trader[]>('/api/traders')

export const createTrader = (data: TraderUpsertRequest) =>
  request<Trader>('/api/traders', { method: 'POST', body: JSON.stringify(data) })

export const updateTrader = (id: number, data: TraderUpsertRequest) =>
  request<Trader>(`/api/traders/${id}`, { method: 'PUT', body: JSON.stringify(data) })

export const deleteTrader = (id: number) => request<void>(`/api/traders/${id}`, { method: 'DELETE' })

/** 启停按状态幂等（200，data=TraderDto）。 */
export const startTrader = (id: number) =>
  request<Trader>(`/api/traders/${id}/start`, { method: 'POST' })

export const stopTrader = (id: number) =>
  request<Trader>(`/api/traders/${id}/stop`, { method: 'POST' })

/** 手动平仓：quantity/price 为字符串金额；order=null 时读 errorCode/error 解释被拒原因。 */
export const closePosition = (id: number, data: ManualCloseRequest) =>
  request<ManualCloseResultDto>(`/api/traders/${id}/close-position`, {
    method: 'POST',
    body: JSON.stringify(data),
  })

/** 旧签名保留（DashboardPage 使用）：市价平仓，数量由后端按当前持仓推导。 */
export const closeTraderPosition = (id: number, symbol: string) =>
  closePosition(id, { symbol, orderType: 'market', reason: '手动平仓' })

/** 旧看板形状（当前页面使用）；新链路请用 fetchTraderDashboard。 */
export const getTraderDashboard = (id: number) => request<TraderDashboard>(`/api/traders/${id}/dashboard`)

/** 新链路看板：含 latestCycle，金额均为字符串（Contracts/TraderContracts.cs TraderDashboardDto）。 */
export const fetchTraderDashboard = (id: number) =>
  request<TraderDashboardDto>(`/api/traders/${id}/dashboard`)

function paging(page: number, pageSize: number): string {
  return `page=${encodeURIComponent(String(page))}&pageSize=${encodeURIComponent(String(pageSize))}`
}

/** 周期列表（分页 §1.3：page 从 1 起、pageSize 1..100）。 */
export const listTraderCycles = (traderId: number, page = 1, pageSize = 20) =>
  request<PagedDto<CycleDto>>(`/api/traders/${traderId}/cycles?${paging(page, pageSize)}`)

/** 复盘列表（持久 reviewer 阶段行，含 skipped/failed 留痕）。 */
export const listTraderReviews = (traderId: number, page = 1, pageSize = 20) =>
  request<PagedDto<ReviewListItemDto>>(`/api/traders/${traderId}/reviews?${paging(page, pageSize)}`)

/** 手动触发复盘：60 秒内重复触发抛 409 review_in_progress。 */
export const runTraderReview = (traderId: number) =>
  request<ReviewRunDto>(`/api/traders/${traderId}/reviews/run`, { method: 'POST' })

/** 经验列表（分页；正文 JSON 数组已由后端解析为 string[]）。 */
export const listTraderLessons = (traderId: number, page = 1, pageSize = 20) =>
  request<PagedDto<LessonDto>>(`/api/traders/${traderId}/lessons?${paging(page, pageSize)}`)

// —— 周期 / 阶段 ——

/** 周期详情（周期、阶段、提案、裁决仅 open 路径、订单）。 */
export const getCycle = (cycleId: string) =>
  request<CycleDetailDto>(`/api/cycles/${encodeURIComponent(cycleId)}`)

/** 阶段全文（transcript 原文 + 工具调用摘要）。 */
export const getCycleStage = (cycleId: string, stageRunId: string) =>
  request<StageDetailDto>(
    `/api/cycles/${encodeURIComponent(cycleId)}/stages/${encodeURIComponent(stageRunId)}`,
  )

/** 旧余额读取（当前表单使用，按数字使用）；新链路金额为字符串，请用 fetchExchangeBalance。 */
export const getExchangeBalance = (id: number) =>
  request<{ usdt: number; available: number }>(`/api/exchanges/${id}/balance`)

// —— SSE ——

/** 连接状态：EventSource 自动重连期间为 reconnecting；fetch 流正常结束为 closed。 */
export type ConnectionState = 'connecting' | 'open' | 'error' | 'reconnecting' | 'closed'

export interface StreamOptions {
  /** 连接状态变化（UI 可据此显示横幅；重连成功回到 open 时重新拉取 dashboard）。 */
  onStatus?: (state: ConnectionState) => void
  /** 单帧解析失败等非致命错误（流继续消费，不抛出）。 */
  onError?: (error: Error) => void
}

export interface TestRunStreamOptions extends StreamOptions {
  /** 外部取消（组件卸载）；触发后与 disposer 等效。 */
  signal?: AbortSignal
}

/** LC-STD-03 §4.2 的事件名全集（顺序即订阅顺序）。 */
export const CYCLE_EVENT_NAMES = [
  'state',
  'cycle',
  'stage',
  'proposal',
  'verdict',
  'tool',
  'order',
  'equity',
  'position-history',
  'ping',
] as const

export type CycleEventName = (typeof CYCLE_EVENT_NAMES)[number]

export interface SseFrame {
  event: string
  data: string
}

/**
 * 从累积缓冲区切出完整 SSE 帧（以空行结束；兼容 CRLF 与多行 data），返回未闭合的 rest。
 * 交易员 EventSource 无需本函数（浏览器已解析），POST test-run 流复用这一份实现。
 */
export function parseSseFrames(buffer: string): { frames: SseFrame[]; rest: string } {
  const frames: SseFrame[] = []
  let rest = buffer
  for (;;) {
    const boundary = /\r?\n\r?\n/.exec(rest)
    if (!boundary) break
    const raw = rest.slice(0, boundary.index)
    rest = rest.slice(boundary.index + boundary[0].length)
    const frame = parseSseFrame(raw)
    if (frame) frames.push(frame)
  }
  return { frames, rest }
}

/** 单帧解析：忽略注释行；多行 data 按 SSE 规范用 \n 连接。 */
function parseSseFrame(raw: string): SseFrame | null {
  let event = 'message'
  const data: string[] = []
  for (const line of raw.split(/\r?\n/)) {
    if (line === '' || line.startsWith(':')) continue
    const colon = line.indexOf(':')
    const field = colon < 0 ? line : line.slice(0, colon)
    let value = colon < 0 ? '' : line.slice(colon + 1)
    if (value.startsWith(' ')) value = value.slice(1)
    if (field === 'event') event = value
    else if (field === 'data') data.push(value)
  }
  return data.length > 0 ? { event, data: data.join('\n') } : null
}

function isCycleEventName(name: string): name is CycleEventName {
  return (CYCLE_EVENT_NAMES as readonly string[]).includes(name)
}

/** 事件名注入 type，data JSON 原样展开；坏帧只回调 onError 并跳过，绝不让流中断。 */
function dispatchCycleFrame(
  name: CycleEventName,
  raw: string,
  onEvent: (event: CycleEvent) => void,
  onError?: (error: Error) => void,
): void {
  let payload: unknown
  try {
    payload = JSON.parse(raw)
  } catch {
    onError?.(new Error(`${name} 事件载荷不是合法 JSON（已跳过该帧）`))
    return
  }
  if (payload === null || typeof payload !== 'object' || Array.isArray(payload)) {
    onError?.(new Error(`${name} 事件载荷不是对象（已跳过该帧）`))
    return
  }
  onEvent({ ...(payload as Record<string, unknown>), type: name } as unknown as CycleEvent)
}

/**
 * 交易员实时流（EventSource，GET SSE）：订阅 §4.2 全部事件名。
 * 返回 disposer：移除全部监听并关闭连接（组件卸载时调用，无监听泄漏）。
 * EventSource 自动重连；重连成功后服务端会补发 state 种子帧，消费者应在 open 时重拉 dashboard。
 */
export function openTraderStream(
  traderId: number,
  onEvent: (event: CycleEvent) => void,
  options: StreamOptions = {},
): () => void {
  const source = new EventSource(`/api/traders/${traderId}/stream`)
  options.onStatus?.('connecting')

  const listeners: Array<[CycleEventName, (event: Event) => void]> = CYCLE_EVENT_NAMES.map((name) => {
    const listener = (event: Event) => {
      const message = event as MessageEvent<string>
      dispatchCycleFrame(name, typeof message.data === 'string' ? message.data : '', onEvent, options.onError)
    }
    source.addEventListener(name, listener)
    return [name, listener]
  })

  source.onopen = () => options.onStatus?.('open')
  source.onerror = () => {
    // CONNECTING = 浏览器正在自动重连；CLOSED = 已放弃（如服务端持续 404）。
    options.onStatus?.(source.readyState === EventSource.CONNECTING ? 'reconnecting' : 'error')
  }

  return () => {
    for (const [name, listener] of listeners) source.removeEventListener(name, listener)
    source.close()
  }
}

/**
 * 试运行流（POST /api/strategies/test-run，SSE over fetch + ReadableStream）：
 * POST 无法用 EventSource，故自行解析帧；事件集 state/cycle/stage/proposal/verdict/tool/ping（无 order）。
 * 返回 disposer：中止 fetch 并取消读取；外部 signal 触发时等价于 disposer。
 */
export function openTestRunStream(
  body: TestRunRequest,
  onEvent: (event: CycleEvent) => void,
  options: TestRunStreamOptions = {},
): () => void {
  const controller = new AbortController()
  let disposed = false
  const abort = () => {
    if (disposed) return
    disposed = true
    controller.abort()
  }

  const external = options.signal
  if (external) {
    if (external.aborted) abort()
    else external.addEventListener('abort', abort, { once: true })
  }

  void (async () => {
    try {
      options.onStatus?.('connecting')
      const response = await fetch('/api/strategies/test-run', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
        signal: controller.signal,
      })
      if (!response.ok) {
        throw apiError(response.status, await response.text().catch(() => ''))
      }
      if (!response.body) {
        throw new ApiRequestError('浏览器不支持流式读取', null, response.status)
      }

      options.onStatus?.('open')
      const reader = response.body.getReader()
      const decoder = new TextDecoder()
      let buffer = ''

      for (;;) {
        const { done, value } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        const parsed = parseSseFrames(buffer)
        buffer = parsed.rest
        for (const frame of parsed.frames) {
          if (isCycleEventName(frame.event)) {
            dispatchCycleFrame(frame.event, frame.data, onEvent, options.onError)
          }
        }
      }
      // 流尾可能残留一个没有空行收尾的半帧；补一个边界再解析一次，避免丢最后一帧
      if (!disposed && buffer.trim().length > 0) {
        for (const frame of parseSseFrames(`${buffer}\n\n`).frames) {
          if (isCycleEventName(frame.event)) {
            dispatchCycleFrame(frame.event, frame.data, onEvent, options.onError)
          }
        }
      }
      if (!disposed) options.onStatus?.('closed')
    } catch (error) {
      if (disposed) return
      if (error instanceof DOMException && error.name === 'AbortError') return
      options.onError?.(error instanceof Error ? error : new Error('试运行流请求失败'))
      options.onStatus?.('error')
    }
  })()

  return () => {
    external?.removeEventListener('abort', abort)
    abort()
  }
}
