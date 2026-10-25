// 后端以 camelCase 枚举字符串序列化（Contracts/ApiJson.cs：JsonStringEnumConverter(CamelCase)）。
export type ProviderType = 'openAICompatible' | 'azureOpenAI' | 'ollama'

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
  /** 工具调用验证结果（null=未验证）；启动交易员与 test-run 前置校验依赖本字段（Contracts/ProviderContracts.cs ProviderDto） */
  toolCallingSupported: boolean | null
  /** 最近一次工具调用验证时间（同上） */
  toolCallingVerifiedAt: string | null
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
  openAICompatible: 'OpenAI 兼容',
  azureOpenAI: 'Azure OpenAI',
  ollama: 'Ollama',
}

export type ExchangeType = 'binance' | 'bybit' | 'okx' | 'bitget' | 'gateIo' | 'kucoin'

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

/** Contracts/StrategyConfigModels.cs RoleInstructionConfig：五角色追加指令（拼接在对应提示词末尾，各 ≤2,000 字）。 */
export interface RoleInstructions {
  analyst: string
  trader: string
  risk: string
  executor: string
  reviewer: string
}

/** Contracts/StrategyConfigModels.cs AgentBudgetConfig：阶段超时、工具上限与单周期输入 token 预算。 */
export interface AgentBudgetConfig {
  analystTimeoutSeconds: number
  traderTimeoutSeconds: number
  riskTimeoutSeconds: number
  executorTimeoutSeconds: number
  cycleTimeoutSeconds: number
  analystToolLimit: number
  traderToolLimit: number
  riskToolLimit: number
  executorToolLimit: number
  cycleToolLimit: number
  inputTokenBudget: number
}

/** Contracts/StrategyConfigModels.cs ReviewConfig：每日复盘调度配置（localTime 为 HH:mm）。 */
export interface ReviewConfig {
  enabled: boolean
  localTime: string
  maxLookbackDays: number
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
  roleInstructions: RoleInstructions
  agentBudget: AgentBudgetConfig
  review: ReviewConfig
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

// —— 新链路（Task 14 B 组）：统一包裹 / 周期 / 阶段 / 提案 / 裁决 / 订单 / SSE ——
// 权威来源：Contracts/CycleContracts.cs、Contracts/TraderContracts.cs、Workflows/CycleEvents.cs。
// 金额、数量、百分比、杠杆一律字符串（LC-STD-03 §1.1）；时间一律 UTC 字符串；null=未知/未提供。

/** 统一 API 包裹（Contracts/ApiResponse.cs）：成功只带 data、失败只带 error；data 内部 null 是业务语义。 */
export interface ApiResponse<T> {
  success: boolean
  data?: T
  error?: { code: string; message: string }
}

/** 周期状态（Endpoints/CyclesEndpoints.cs CycleDtoMapper.CycleStatuses，LC-STD-03 §4.2）。 */
export type CycleStatus = 'running' | 'completed' | 'failed' | 'skipped' | 'aborted'

/** 阶段角色（Agents/AgentRole.cs：analyst/trader/risk/executor/reviewer）。 */
export type StageRole = 'analyst' | 'trader' | 'risk' | 'executor' | 'reviewer'

/** 阶段落库状态（Models/AgentStageRun.cs；in_progress 只出现在复盘结果层，不是阶段行状态）。 */
export type StageStatus = 'completed' | 'failed' | 'skipped'

/** 周期触发来源（Workflows/CycleEvents.cs、Endpoints/TradersEndpoints.cs：schedule/manual/test）。 */
export type TriggerType = 'schedule' | 'manual' | 'test'

/** 执行模式（Models/Trader.cs ExecutionMode：dry/live）。 */
export type ExecutionMode = 'dry' | 'live'

/** 提案动作（Models/TradeProposal.cs Action、PromptLibrary 输出契约：open/close/hold）。 */
export type ProposalAction = 'open' | 'close' | 'hold'

/** 风控裁决（Services/CycleRecorder.cs RecordVerdict：approved/rejected）。 */
export type RiskDecision = 'approved' | 'rejected'

/** 订单生命周期状态（Models/TraderOrder.cs、LC-STD-03 §2.2：含 cancelled；cancelled 仅用于撤单成功）。 */
export type OrderStatus = 'submitted' | 'filled' | 'partial' | 'failed' | 'unknown' | 'cancelled'

/** Contracts/CycleContracts.cs CycleInfoDto：GET /api/cycles/{id} 的 cycle 块与周期列表项。 */
export interface CycleDto {
  id: string
  traderId: number | null
  strategyId: number
  providerId: number | null
  exchangeAccountId: number | null
  mode: ExecutionMode
  trigger: TriggerType
  status: CycleStatus
  startedAt: string
  completedAt: string | null
  errorCode: string | null
  error: string | null
  inputTokens: number | null
  outputTokens: number | null
  elapsedMs: number | null
}

/** Contracts/CycleContracts.cs CycleStageDto：GET /api/cycles/{id} 的 stages[] 单项。 */
export interface StageDto {
  id: string
  role: StageRole
  status: StageStatus
  elapsedMs: number
  inputTokens: number | null
  outputTokens: number | null
  toolCalls: number
  summary: string | null
  error: string | null
}

/** Contracts/CycleContracts.cs TradeProposalDto：周期详情 proposal 与 SSE proposal 事件共用。 */
export interface ProposalDto {
  id: string
  action: ProposalAction
  symbol: string | null
  side: 'long' | 'short' | null
  confidence: string | null
  entryPrice: string | null
  stopLossPrice: string | null
  takeProfitPrice: string | null
  positionPct: string | null
  leverage: string | null
  thesis: string
  invalidation: string | null
}

/** Contracts/CycleContracts.cs CycleVerdictDto：周期详情 verdict（仅 open 路径返回，无 cycleId/proposalId）。 */
export interface VerdictDto {
  id: string
  decision: RiskDecision
  reasons: string[]
  maxPositionPct: string | null
  maxLeverage: string | null
  entryZoneMin: string | null
  entryZoneMax: string | null
  allowMarket: boolean | null
  validUntilUtc: string | null
}

/**
 * Contracts/CycleContracts.cs CycleOrderDto：GET /api/cycles/{id} 的 orders[] 与 SSE order 事件。
 * 与 RecentOrderDto（看板 recentOrders 的旧链路形状）不是同一种订单，不可互换。
 */
export interface CycleOrderDto {
  orderId: number
  cycleId: string | null
  traderId: number | null
  exchangeAccountId: number | null
  proposalId: string | null
  action: string
  side: string | null
  symbol: string | null
  quantity: string | null
  filledQuantity: string | null
  price: string | null
  averagePrice: string | null
  status: OrderStatus | null
  simulated: boolean
  note: string | null
  at: string
}

/**
 * Contracts/TraderContracts.cs TraderOrderDto：GET /api/traders/{id}/dashboard 的 recentOrders[]（旧链路形状）。
 * 只有 id/quantity/price/note/error；没有 orderId/filledQuantity/status/simulated 等新链路字段。
 */
export interface RecentOrderDto {
  id: number
  at: string
  symbol: string | null
  action: string
  quantity: string | null
  price: string | null
  note: string | null
  error: string | null
}

/** Contracts/CycleContracts.cs CycleDetailDto：GET /api/cycles/{id} 的 data，也是看板 latestCycle 的形状。 */
export interface CycleDetailDto {
  cycle: CycleDto
  stages: StageDto[]
  proposal: ProposalDto | null
  verdict: VerdictDto | null
  orders: CycleOrderDto[]
}

/** Contracts/CycleContracts.cs PagedResult<T>：page 从 1 起；空列表 items=[]、total=0，不得为 null。 */
export interface PagedDto<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

/** Contracts/CycleContracts.cs StageToolCallDto：阶段全文里的工具调用摘要（不含入参/回执全文）。 */
export interface StageToolCallDto {
  toolName: string
  success: boolean
  errorCode: string | null
  elapsedMs: number
  truncated: boolean
  at: string
}

/** Contracts/CycleContracts.cs StageDetailDto：GET /api/cycles/{id}/stages/{stageRunId} 的 data。 */
export interface StageDetailDto {
  id: string
  cycleId: string | null
  traderId: number | null
  role: StageRole
  attempt: number
  status: StageStatus
  providerName: string
  model: string
  instructions: string
  transcriptJson: string
  outputJson: string | null
  reasoning: string | null
  inputTokens: number | null
  outputTokens: number | null
  elapsedMs: number
  toolCallCount: number
  error: string | null
  truncated: boolean
  createdAt: string
  toolCalls: StageToolCallDto[]
}

/** Contracts/CycleContracts.cs ManualCloseRequest：POST /api/traders/{id}/close-position 请求体（金额字符串）。 */
export interface ManualCloseRequest {
  symbol: string
  quantity?: string | null
  orderType: 'market' | 'limit'
  price?: string | null
  reason: string
}

/** Contracts/CycleContracts.cs ManualCloseResultDto：order=null 时带出周期 errorCode/error 解释「什么都没发生」。 */
export interface ManualCloseResultDto {
  cycleId: string
  order: CycleOrderDto | null
  errorCode: string | null
  error: string | null
}

/** Contracts/CycleContracts.cs ReviewRunDto：POST /api/traders/{id}/reviews/run 的 data（触发成功≠复盘成功）。 */
export type ReviewRunStatus = 'completed' | 'failed' | 'skipped'

export interface ReviewRunDto {
  status: ReviewRunStatus
  errorCode: string | null
  stageRunId: string | null
  elapsedMs: number
  periodStart: string | null
  periodEnd: string | null
}

/** Contracts/CycleContracts.cs ReviewListItemDto：GET /api/traders/{id}/reviews 列表项。 */
export interface ReviewListItemDto {
  id: string
  status: StageStatus
  attempt: number
  providerName: string
  model: string
  elapsedMs: number
  inputTokens: number | null
  outputTokens: number | null
  toolCalls: number
  error: string | null
  createdAt: string
}

/** Contracts/CycleContracts.cs LessonDto：GET /api/traders/{id}/lessons 列表项（正文 JSON 数组已解析）。 */
export interface LessonDto {
  id: number
  traderId: number
  stageRunId: string
  periodStart: string
  periodEnd: string
  summary: string
  strengths: string[]
  mistakes: string[]
  rules: string[]
  evidence: string[]
  createdAt: string
}

// —— SSE 载荷（LC-STD-03 §4.2；event: 帧名 + 单行 data JSON）——

/** Contracts/CycleContracts.cs TraderStateEvent：连接建立与 start/stop 触发。 */
export interface StateEventPayload {
  isRunning: boolean
  lastCycleStatus: CycleStatus | null
  lastCycleAt: string | null
}

/** Workflows/CycleEvents.cs CycleStartedEvent/CycleCompletedEvent：不是 CycleDto（无 strategyId/errorCode/token/elapsed）。 */
export interface CycleEventPayload {
  cycleId: string
  traderId: number | null
  mode: ExecutionMode
  trigger: TriggerType
  status: CycleStatus
  startedAt: string
  completedAt: string | null
  error: string | null
}

/** Workflows/CycleEvents.cs StageRecordedEvent：扁平载荷（无嵌套 StageDto、无 id 字段，阶段 id 名为 stageId）。 */
export interface StageEventPayload {
  cycleId: string
  stageId: string
  role: StageRole
  status: StageStatus
  attempt: number
  elapsedMs: number
  inputTokens: number | null
  outputTokens: number | null
  toolCalls: number
  summary: string | null
  error: string | null
}

/** Workflows/CycleEvents.cs VerdictRecordedEvent：比 CycleVerdictDto 多 cycleId/proposalId。 */
export interface VerdictEventPayload {
  id: string
  cycleId: string
  proposalId: string
  decision: RiskDecision
  reasons: string[]
  maxPositionPct: string | null
  maxLeverage: string | null
  entryZoneMin: string | null
  entryZoneMax: string | null
  allowMarket: boolean | null
  validUntilUtc: string | null
}

/** Tools/ToolAuditor.cs：写工具调用或工具失败；error 仅在 success=false 时非空（固定中文摘要）。
 * cycleId 可为 null——复盘工具调用发往交易员频道时没有周期（LC-STD-03 §2.1）。 */
export interface ToolEventPayload {
  cycleId: string | null
  stageId: string
  tool: string
  success: boolean
  errorCode: string | null
  error: string | null
  elapsedMs: number
  at: string
}

/** Endpoints/TradersEndpoints.cs：账户权益采样（金额字符串），仅该交易员绑定账户。 */
export interface EquityEventPayload {
  equity: string
  at: string
}

/** Contracts/CycleContracts.cs PositionHistoryEventDto：平仓历史同步（盈亏字符串）。 */
export interface PositionHistoryEventPayload {
  positionHistoryId: number
  symbol: string
  realizedPnl: string
  closeTime: string
}

/** Endpoints/TradersEndpoints.cs 心跳帧：{ at }。 */
export interface PingEventPayload {
  at: string
}

/** LC-STD-03 §4.2 的完整 SSE 事件集：type 由 event: 帧名注入，其余字段与 data JSON 逐字对齐。 */
export type CycleEvent =
  | ({ type: 'state' } & StateEventPayload)
  | ({ type: 'cycle' } & CycleEventPayload)
  | ({ type: 'stage' } & StageEventPayload)
  | ({ type: 'proposal' } & ProposalDto)
  | ({ type: 'verdict' } & VerdictEventPayload)
  | ({ type: 'tool' } & ToolEventPayload)
  | ({ type: 'order' } & CycleOrderDto)
  | ({ type: 'equity' } & EquityEventPayload)
  | ({ type: 'position-history' } & PositionHistoryEventPayload)
  | ({ type: 'ping' } & PingEventPayload)

// —— 看板 DTO（Contracts/TraderContracts.cs TraderDashboardDto；金额已字符串化）——
// 旧 Dashboard* 接口保留给当前页面，名称加 Dto 后缀以区分，不可互换。

/** Contracts/TraderContracts.cs DashboardTraderInfo：看板 trader 块（lastCycleStatus 已按白名单收敛）。 */
export interface DashboardTraderInfoDto {
  id: number
  name: string
  providerName: string
  model: string
  exchangeLabel: string
  accountName: string
  strategyName: string
  strategyType: string
  isRunning: boolean
  executionMode: ExecutionMode
  isCrossMargin: boolean
  scanIntervalMinutes: number
  initialBalance: string | null
  startedAt: string | null
  lastCycleAt: string | null
  lastCycleStatus: CycleStatus | null
  capabilityCanTrade: boolean
  capabilityNotes: string[]
  positionMode: string | null
}

/** Contracts/TraderContracts.cs DashboardAccount：账户块（金额字符串）。 */
export interface DashboardAccountDto {
  success: boolean
  error: string | null
  equity: string | null
  available: string | null
  totalPnl: string | null
  pnlPct: string | null
  positionCount: number
  marginUsedPct: string | null
  at: string
}

/** Contracts/TraderContracts.cs DashboardSample：净值样本（金额字符串）。 */
export interface DashboardSampleDto {
  at: string
  equity: string
}

/** Contracts/TraderContracts.cs DashboardPosition：持仓（数量与价格字符串）。 */
export interface DashboardPositionDto {
  symbol: string
  side: string
  quantity: string
  averageOpenPrice: string
  unrealizedPnl: string | null
  liquidationPrice: string | null
  leverage: string | null
}

/** Contracts/TraderContracts.cs DashboardDecision：旧 DecisionRecord 链路（P7 移除），数值字符串。 */
export interface DashboardDecisionDto {
  id: number
  at: string
  action: string | null
  symbol: string | null
  confidence: string | null
  entryPrice: string | null
  stopLossPct: string | null
  takeProfitPct: string | null
  positionPct: string | null
  leverage: string | null
  reason: string | null
  reasoning: string | null
  inputTokens: number | null
  outputTokens: number | null
  elapsedMs: number | null
  traderId: number | null
  systemPrompt: string | null
  userPrompt: string | null
  status: string
  failureStage: string | null
  error: string | null
  rawOutput: string | null
  promptRevision: string | null
  requestSent: boolean | null
}

/** Contracts/TraderContracts.cs DashboardMetrics：统计指标（除 totalTrades 外均为字符串）。 */
export interface DashboardMetricsDto {
  totalTrades: number
  winRate: string
  totalPnl: string
  totalFee: string
  netPnl: string
  profitFactor: string
  avgWin: string
  avgLoss: string
  maxDrawdownPct: string
  sharpe: string
}

/** Contracts/TraderContracts.cs DashboardDirectionStat。 */
export interface DashboardDirectionStatDto {
  side: string
  count: number
  winRate: string
  totalPnl: string
  avgPnl: string
}

/** Contracts/TraderContracts.cs DashboardSymbolStat。 */
export interface DashboardSymbolStatDto {
  symbol: string
  count: number
  winRate: string
  totalPnl: string
}

/** Contracts/TraderContracts.cs DashboardHistoryRow：历史仓位行（金额与数量字符串）。 */
export interface DashboardHistoryRowDto {
  symbol: string
  side: string | null
  entryPrice: string | null
  exitPrice: string | null
  quantity: string | null
  leverage: string | null
  realizedPnl: string
  fee: string | null
  closeTime: string
}

/** Contracts/TraderContracts.cs DashboardHistory：历史仓位聚合。 */
export interface DashboardHistoryDto {
  success: boolean
  error: string | null
  metrics: DashboardMetricsDto
  directions: DashboardDirectionStatDto[]
  symbols: DashboardSymbolStatDto[]
  rows: DashboardHistoryRowDto[]
  note: string | null
}

/**
 * Contracts/TraderContracts.cs TraderDashboardDto：GET /api/traders/{id}/dashboard 的 data。
 * recentOrders 是旧链路 RecentOrderDto，latestCycle.orders 是新链路 CycleOrderDto；两者不可混用。
 */
export interface TraderDashboardDto {
  trader: DashboardTraderInfoDto
  account: DashboardAccountDto | null
  samples: DashboardSampleDto[]
  positions: DashboardPositionDto[]
  decisions: DashboardDecisionDto[]
  history: DashboardHistoryDto
  recentOrders: RecentOrderDto[]
  latestCycle: CycleDetailDto | null
}

// —— 系统 / 余额 / 提示词预览 ——

/** Endpoints/SystemEndpoints.cs GET /api/health：只含公开运行状态字段。 */
export interface HealthDto {
  status: 'ok' | 'degraded'
  version: string
  database: boolean
  uptimeSeconds: number
  timestamp: string
}

/** Endpoints/ExchangesEndpoints.cs GET /api/exchanges/{id}/balance：金额字符串。 */
export interface ExchangeBalanceDto {
  usdt: string
  available: string
}

/** Contracts/StrategyContracts.cs RolePromptPreviewDto：preview-prompts 单角色产物（不调用模型）。 */
export interface RolePromptPreview {
  role: StageRole
  prompt: string
  estimatedTokens: number
}

/** Contracts/StrategyContracts.cs PromptPreviewResultDto。 */
export interface PromptPreviewResult {
  roles: RolePromptPreview[]
}

/** Contracts/StrategyContracts.cs StrategyValidationDto：通过时 200；不通过走 400 validation_failed。 */
export interface StrategyValidationResult {
  valid: boolean
  errors: string[]
}

/** Contracts/StrategyContracts.cs StrategyRunTestRequest：POST /api/strategies/test-run 请求体（SSE 演练）。 */
export interface TestRunRequest {
  strategyId: number
  providerId: number
  /** 契约预留：B 组只支持合成账户演练，必须为空/省略 */
  accountId?: number | null
  /** 金额字符串；省略时后端按 "10000" 处理 */
  testEquity?: string | null
  includeMemory?: boolean
}
