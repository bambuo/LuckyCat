import type {
  ExchangeAccount,
  ExchangeUpsertRequest,
  ExchangeVerifyEvent,
  OutboundIp,
  PromptPreview,
  Provider,
  ProviderUpsertRequest,
  Strategy,
  StrategyConfig,
  StrategyDefaults,
  StrategyTestResult,
  StrategyUpsertRequest,
  Trader,
  TraderDashboard,
  TraderUpsertRequest,
  VerifyEvent,
} from './types'

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  })
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.message ?? `请求失败（HTTP ${res.status}）`)
  }
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}

export const listProviders = () => request<Provider[]>('/api/providers')

export const createProvider = (data: ProviderUpsertRequest) =>
  request<Provider>('/api/providers', { method: 'POST', body: JSON.stringify(data) })

export const updateProvider = (id: number, data: ProviderUpsertRequest) =>
  request<Provider>(`/api/providers/${id}`, { method: 'PUT', body: JSON.stringify(data) })

export const deleteProvider = (id: number) =>
  request<void>(`/api/providers/${id}`, { method: 'DELETE' })

export const listExchanges = () => request<ExchangeAccount[]>('/api/exchanges')

export const createExchange = (data: ExchangeUpsertRequest) =>
  request<ExchangeAccount>('/api/exchanges', { method: 'POST', body: JSON.stringify(data) })

export const updateExchange = (id: number, data: ExchangeUpsertRequest) =>
  request<ExchangeAccount>(`/api/exchanges/${id}`, { method: 'PUT', body: JSON.stringify(data) })

export const deleteExchange = (id: number) =>
  request<void>(`/api/exchanges/${id}`, { method: 'DELETE' })

export const getOutboundIp = () => request<OutboundIp>('/api/exchanges/outbound-ip')

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

export const previewStrategyPrompt = (config: StrategyConfig) =>
  request<PromptPreview>('/api/strategies/preview-prompt', {
    method: 'POST',
    body: JSON.stringify({ config }),
  })

// 最长 120s（后端超时），前端 fetch 无默认超时；strategyId 用于历史决策记忆的读取与落库
export const runStrategyTest = (config: StrategyConfig, providerId: number, strategyId: number | null) =>
  request<StrategyTestResult>('/api/strategies/test-run', {
    method: 'POST',
    body: JSON.stringify({ config, providerId, strategyId }),
  })

// —— AI 交易员 ——

export const listTraders = () => request<Trader[]>('/api/traders')

export const createTrader = (data: TraderUpsertRequest) =>
  request<Trader>('/api/traders', { method: 'POST', body: JSON.stringify(data) })

export const updateTrader = (id: number, data: TraderUpsertRequest) =>
  request<Trader>(`/api/traders/${id}`, { method: 'PUT', body: JSON.stringify(data) })

export const deleteTrader = (id: number) => request<void>(`/api/traders/${id}`, { method: 'DELETE' })

export const startTrader = (id: number) => request<{ running: boolean }>(`/api/traders/${id}/start`, { method: 'POST' })

export const stopTrader = (id: number) => request<{ running: boolean }>(`/api/traders/${id}/stop`, { method: 'POST' })

export const closeTraderPosition = (id: number, symbol: string) =>
  request<{ closed: boolean }>(`/api/traders/${id}/close-position`, {
    method: 'POST',
    body: JSON.stringify({ symbol }),
  })

export const getTraderDashboard = (id: number) => request<TraderDashboard>(`/api/traders/${id}/dashboard`)

/** 真实读取账户 USDT 余额（交易员「获取当前余额」） */
export const getExchangeBalance = (id: number) =>
  request<{ usdt: number; available: number }>(`/api/exchanges/${id}/balance`)
