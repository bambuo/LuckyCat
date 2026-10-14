export type ProviderType = 'OpenAICompatible' | 'AzureOpenAI' | 'Ollama'

export type ThinkingMode = 'default' | 'on' | 'off'

export interface Provider {
  id: number
  name: string
  type: ProviderType
  baseUrl: string
  apiKeyMasked: string
  model: string
  apiVersion: string | null
  thinkingMode: ThinkingMode
  contextTokens: number
  maxOutputTokens: number
  autoCompressContext: boolean
  createdAt: string
  updatedAt: string
}

export interface ProviderUpsertRequest {
  name: string
  type: ProviderType
  baseUrl: string
  apiKey: string | null
  model: string
  apiVersion: string | null
  thinkingMode: ThinkingMode
  contextTokens: number
  maxOutputTokens: number
  autoCompressContext: boolean
}

export interface VerifyEvent {
  type: 'start' | 'chunk' | 'result' | 'done'
  check?: 'basic' | 'streaming' | 'tools' | 'json'
  status?: 'pass' | 'fail'
  elapsedMs?: number
  detail?: string
  evidence?: string
  count?: number
  ttftMs?: number
  passed?: number
  failed?: number
}

export const PROVIDER_TYPE_LABELS: Record<ProviderType, string> = {
  OpenAICompatible: 'OpenAI 兼容',
  AzureOpenAI: 'Azure OpenAI',
  Ollama: 'Ollama',
}

export type ExchangeType = 'Binance' | 'Bybit' | 'OKX' | 'Bitget' | 'GateIo' | 'Kucoin'

export interface ExchangeAccount {
  id: number
  exchangeType: ExchangeType
  accountName: string
  apiKeyMasked: string
  apiSecretMasked: string
  passphraseMasked: string | null
  /** 交易所用户 ID（UID）：Gate.io 用户数据流 WS 需要；其余为 null */
  userId: string | null
  createdAt: string
  updatedAt: string
}

export interface ExchangeUpsertRequest {
  exchangeType: ExchangeType
  accountName: string
  apiKey: string | null
  apiSecret: string | null
  passphrase: string | null
  userId: string | null
}

export interface ExchangeVerifyEvent {
  type: 'start' | 'result' | 'done'
  check?: 'connect' | 'auth'
  status?: 'pass' | 'fail'
  elapsedMs?: number
  detail?: string
  evidence?: string
  passed?: number
  failed?: number
}

export interface OutboundIp {
  ip: string | null
}

// —— 策略工作室 ——

export type StrategyType = 'ai_trading' | 'grid_trading'
export type StrategyVariant = 'balanced' | 'aggressive' | 'conservative' | 'scalping'
export type CoinSourceType = 'static' | 'score' | 'oi_top' | 'oi_low' | 'mixed'
export type CoinSourceExchange = 'binance' | 'gate'
export type GridDistribution = 'uniform' | 'gaussian' | 'pyramid'
export type HttpMethod = 'GET' | 'POST'

export interface CoinSourceConfig {
  sourceType: CoinSourceType
  exchange: CoinSourceExchange
  staticCoins: string[]
  excludedCoins: string[]
  useScore: boolean
  scoreLimit: number
  useOiTop: boolean
  oiTopLimit: number
  useOiLow: boolean
  oiLowLimit: number
}

export interface GridConfig {
  symbol: string
  gridCount: number
  totalInvestment: number
  leverage: number
  upperPrice: number
  lowerPrice: number
  useAtrBounds: boolean
  atrMultiplier: number
  distribution: GridDistribution
  maxDrawdownPct: number
  stopLossPct: number
  dailyLossLimitPct: number
  useMakerOnly: boolean
  enableDirectionAdjust: boolean
  directionBiasRatio: number
}

export interface KlineConfig {
  primaryTimeframe: string
  primaryCount: number
  enableMultiTimeframe: boolean
  selectedTimeframes: string[]
}

export interface IndicatorConfig {
  enabled: boolean
  periods: number[]
}

export interface SentimentConfig {
  enabled: boolean
}

export interface ExternalHeader {
  key: string
  value: string
}

export interface ExternalDataSource {
  name: string
  method: HttpMethod
  url: string
  dataPath: string
  refreshSecs: number
  headers: ExternalHeader[]
}

export interface IndicatorSettings {
  klines: KlineConfig
  enableRawKlines: boolean
  ema: IndicatorConfig
  macd: IndicatorConfig
  rsi: IndicatorConfig
  atr: IndicatorConfig
  boll: IndicatorConfig
  vwap: IndicatorConfig
  volume: SentimentConfig
  openInterest: SentimentConfig
  fundingRate: SentimentConfig
  externalDataSources: ExternalDataSource[]
}

export interface RiskControlConfig {
  maxPositions: number
  btcEthLeverage: number
  altcoinLeverage: number
  btcEthPositionPct: number
  altcoinPositionPct: number
  minRiskRewardRatio: number
  maxMarginUsage: number
  minPositionSize: number
  minConfidence: number
  maxRiskPerTradePct: number
  estimatedFeePct: number
  estimatedSlippagePct: number
  estimatedFundingPct: number
  maxEntryDeviationPct: number
}

export interface PromptSectionConfig {
  role: string
  frequency: string
  entry: string
  workflow: string
}

export interface StrategyConfig {
  strategyType: StrategyType
  coinSource: CoinSourceConfig
  /** 切出网格类型时置 null（对齐设计稿「切出网格 → 清除 grid_config」）；切入时若为 null 填默认值 */
  gridConfig: GridConfig | null
  indicators: IndicatorSettings
  riskControl: RiskControlConfig
  promptSections: PromptSectionConfig
  customPrompt: string
  decisionMemoryEnabled: boolean
  newsEnabled: boolean
  variant: StrategyVariant
}

export interface Strategy {
  id: number
  name: string
  description: string | null
  isActive: boolean
  config: StrategyConfig
  createdAt: string
  updatedAt: string
}

export interface StrategyUpsertRequest {
  name: string
  description: string | null
  config: StrategyConfig
}

export interface StrategyDefaults {
  config: StrategyConfig
  /** 4 段默认正文（用于「已修改」比对与「重置为默认」） */
  promptSections: PromptSectionConfig
}

export interface PromptSummaryItem {
  label: string
  value: string
}

export interface PromptPreview {
  systemPrompt: string
  summary: PromptSummaryItem[]
}

export interface StrategyTestResult {
  elapsedMs: number
  userPrompt: string
  rawOutput: string
  reasoning: string | null
  decision: Record<string, unknown> | null
  dataSource: 'live' | 'incomplete'
  /** 提示词（系统+用户）tokens 粗估 */
  estimatedTokens: number
  /** 上下文超限提示/自动裁剪说明；未超限为 null */
  contextWarning: string | null
  error: string | null
  /** 真实 token 用量（提供商返回时） */
  inputTokens: number | null
  outputTokens: number | null
}

// —— AI 交易员 ——

export type TraderExecutionMode = 'live' | 'dry'

export interface Trader {
  id: number
  name: string
  providerId: number
  providerName: string
  model: string
  exchangeAccountId: number
  exchangeType: ExchangeType
  accountName: string
  strategyId: number
  strategyName: string
  isRunning: boolean
  executionMode: TraderExecutionMode
  isCrossMargin: boolean
  scanIntervalMinutes: number
  initialBalance: number | null
  startedAt: string | null
  lastCycleAt: string | null
  lastCycleStatus: string | null
  createdAt: string
  updatedAt: string
}

export interface TraderUpsertRequest {
  name: string
  providerId: number
  exchangeAccountId: number
  strategyId: number
  isCrossMargin: boolean
  scanIntervalMinutes: number
  initialBalance: number | null
  executionMode: TraderExecutionMode
}

export interface TraderOrder {
  id: number
  at: string
  symbol: string | null
  action: string
  quantity: number | null
  price: number | null
  success: boolean
  note: string | null
  error: string | null
}

// —— 交易员看板 ——

export interface DashboardTraderInfo {
  id: number
  name: string
  providerName: string
  model: string
  exchangeLabel: string
  accountName: string
  strategyName: string
  strategyType: string
  isRunning: boolean
  executionMode: TraderExecutionMode
  isCrossMargin: boolean
  scanIntervalMinutes: number
  initialBalance: number | null
  startedAt: string | null
  lastCycleAt: string | null
  lastCycleStatus: string | null
  capabilityCanTrade: boolean
  capabilityNotes: string[]
  /** 持仓模式：双向 / 单向（不可用时 null） */
  positionMode: string | null
}

export interface DashboardAccount {
  success: boolean
  error: string | null
  equity: number | null
  available: number | null
  totalPnl: number | null
  pnlPct: number | null
  positionCount: number
  marginUsedPct: number | null
  at: string
}

export interface DashboardSample {
  at: string
  equity: number
}

export interface DashboardPosition {
  symbol: string
  side: string
  quantity: number
  averageOpenPrice: number
  unrealizedPnl: number | null
  liquidationPrice: number | null
  leverage: number | null
}

export interface DashboardDecision {
  id: number
  at: string
  action: string | null
  symbol: string | null
  confidence: number | null
  entryPrice: number | null
  stopLossPct: number | null
  takeProfitPct: number | null
  positionPct: number | null
  leverage: number | null
  reason: string | null
  reasoning: string | null
  inputTokens: number | null
  outputTokens: number | null
  elapsedMs: number | null
  traderId: number | null
  /** 本轮系统提示词原文（历史记录可为 null） */
  systemPrompt: string | null
  /** 本轮用户提示词原文 */
  userPrompt: string | null
  status: string
  failureStage: string | null
  error: string | null
  rawOutput: string | null
  promptRevision: string | null
  requestSent: boolean | null
}

export interface DashboardMetrics {
  totalTrades: number
  winRate: number
  totalPnl: number
  totalFee: number
  netPnl: number
  profitFactor: number
  avgWin: number
  avgLoss: number
  maxDrawdownPct: number
  sharpe: number
}

export interface DashboardDirectionStat {
  side: string
  count: number
  winRate: number
  totalPnl: number
  avgPnl: number
}

export interface DashboardSymbolStat {
  symbol: string
  count: number
  winRate: number
  totalPnl: number
}

export interface DashboardHistoryRow {
  symbol: string
  /** 方向（原生收入流水回退时不可得，为 null） */
  side: string | null
  entryPrice: number | null
  exitPrice: number | null
  quantity: number | null
  leverage: number | null
  realizedPnl: number
  fee: number | null
  closeTime: string
}

export interface DashboardHistory {
  success: boolean
  error: string | null
  metrics: DashboardMetrics
  directions: DashboardDirectionStat[]
  symbols: DashboardSymbolStat[]
  rows: DashboardHistoryRow[]
  /** 数据来源说明（如原生收入流水回退口径） */
  note: string | null
}

export interface TraderDashboard {
  trader: DashboardTraderInfo
  account: DashboardAccount | null
  samples: DashboardSample[]
  positions: DashboardPosition[]
  decisions: DashboardDecision[]
  history: DashboardHistory
  recentOrders: TraderOrder[]
}
