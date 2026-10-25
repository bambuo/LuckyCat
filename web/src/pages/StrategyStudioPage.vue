<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Message, Modal } from '@arco-design/web-vue'
import { IconPlayArrow, IconStar } from '@arco-design/web-vue/es/icon'
import {
  ApiRequestError,
  activateStrategy,
  createStrategy,
  deleteStrategy,
  duplicateStrategy,
  getStrategyDefaults,
  listProviders,
  listStrategies,
  openTestRunStream,
  updateStrategy,
} from '../api'
import type { ConnectionState } from '../api'
import LuckyCatScene from '../components/trader/LuckyCatScene.vue'
import StrategyEditor from '../components/strategy/StrategyEditor.vue'
import StrategyListPanel from '../components/strategy/StrategyListPanel.vue'
import RolePromptTabs from '../components/strategy/RolePromptTabs.vue'
import PromptPreviewPanel from '../components/strategy/PromptPreviewPanel.vue'
import ProposalCard from '../components/cycle/ProposalCard.vue'
import StageCard from '../components/cycle/StageCard.vue'
import VerdictCard from '../components/cycle/VerdictCard.vue'
import type {
  AgentBudgetConfig,
  CycleEvent,
  CycleEventPayload,
  CycleStatus,
  ProposalDto,
  Provider,
  RoleInstructions,
  StageDto,
  StageEventPayload,
  Strategy,
  StrategyConfig,
  StrategyDefaults,
  TestRunRequest,
  ToolEventPayload,
  VerdictDto,
  VerdictEventPayload,
} from '../types'

const strategies = ref<Strategy[]>([])
const defaults = ref<StrategyDefaults | null>(null)
const loading = ref(false)
const errorText = ref<string | null>(null)

const selectedId = ref<number | null>(null)
const saved = ref<Strategy | null>(null) // 已保存快照（列表内的权威对象）
const draft = ref<StrategyConfig | null>(null) // 编辑中的草稿（深拷贝；各编辑器就地修改）
const nameInput = ref('')
const descriptionInput = ref('')
const saving = ref(false)

const ROLE_INSTRUCTION_MAX = 2000
const ROLE_KEYS: (keyof RoleInstructions)[] = ['analyst', 'trader', 'risk', 'executor', 'reviewer']
const ROLE_LABELS: Record<keyof RoleInstructions, string> = {
  analyst: '分析师',
  trader: '交易员',
  risk: '风控官',
  executor: '执行员',
  reviewer: '复盘员',
}
const LOCAL_TIME_PATTERN = /^([01]\d|2[0-3]):[0-5]\d$/

interface BudgetField {
  key: Exclude<keyof AgentBudgetConfig, 'inputTokenBudget'>
  label: string
}

const TIMEOUT_FIELDS: BudgetField[] = [
  { key: 'analystTimeoutSeconds', label: '分析师超时（秒）' },
  { key: 'traderTimeoutSeconds', label: '交易员超时（秒）' },
  { key: 'riskTimeoutSeconds', label: '风控官超时（秒）' },
  { key: 'executorTimeoutSeconds', label: '执行员超时（秒）' },
  { key: 'cycleTimeoutSeconds', label: '单周期总超时（秒）' },
]

const TOOL_LIMIT_FIELDS: BudgetField[] = [
  { key: 'analystToolLimit', label: '分析师工具上限' },
  { key: 'traderToolLimit', label: '交易员工具上限' },
  { key: 'riskToolLimit', label: '风控官工具上限' },
  { key: 'executorToolLimit', label: '执行员工具上限' },
  { key: 'cycleToolLimit', label: '单周期工具总上限' },
]

// 深度比对：名称/简介/任何 config 改动都会置脏（修正设计稿缺陷 b）
const isDirty = computed(() => {
  if (!saved.value || !draft.value) return false
  return (
    nameInput.value !== saved.value.name ||
    (descriptionInput.value.trim() || null) !== saved.value.description ||
    JSON.stringify(draft.value) !== JSON.stringify(saved.value.config)
  )
})

const localTimeError = computed(() => {
  const value = draft.value?.review?.localTime ?? ''
  return LOCAL_TIME_PATTERN.test(value) ? '' : '格式应为 HH:mm（00:00–23:59）'
})

function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

/** 补齐新增配置块（老配置/导入文件缺失时回退后端默认）。 */
function normalizeDraftConfig(source: StrategyConfig): StrategyConfig {
  const config = clone(source)
  const base = defaults.value?.config
  if (base) {
    config.roleInstructions = { ...base.roleInstructions, ...config.roleInstructions }
    config.agentBudget = { ...base.agentBudget, ...config.agentBudget }
    config.review = { ...base.review, ...config.review }
  }
  return config
}

async function load() {
  loading.value = true
  errorText.value = null
  try {
    const [list, defaultPayload] = await Promise.all([listStrategies(), getStrategyDefaults()])
    strategies.value = list
    defaults.value = defaultPayload
    if (saved.value) {
      const refreshed = list.find((s) => s.id === saved.value?.id)
      if (refreshed) saved.value = refreshed
      else clearSelection()
    } else if (list.length > 0) {
      // 初始选中：优先激活策略，否则第一条（设计稿 §7）
      selectStrategy(list.find((s) => s.isActive) ?? list[0])
    }
  } catch (err) {
    errorText.value = err instanceof Error ? err.message : '加载失败'
    Message.error(errorText.value)
  } finally {
    loading.value = false
  }
}

// 即点即切：未保存改动直接丢弃（设计稿 §7，无确认）
function selectStrategy(strategy: Strategy) {
  stopTestRun()
  resetTestView()
  selectedId.value = strategy.id
  saved.value = strategy
  draft.value = normalizeDraftConfig(strategy.config)
  nameInput.value = strategy.name
  descriptionInput.value = strategy.description ?? ''
}

function clearSelection() {
  stopTestRun()
  resetTestView()
  testDrawerVisible.value = false
  selectedId.value = null
  saved.value = null
  draft.value = null
  nameInput.value = ''
  descriptionInput.value = ''
}

/** 预算输入被清空时回落后端默认值，再参与校验与保存。 */
function applyBudgetFallbacks() {
  const config = draft.value
  const base = defaults.value?.config.agentBudget
  if (!config || !base) return
  for (const field of [...TIMEOUT_FIELDS, ...TOOL_LIMIT_FIELDS]) {
    const value: unknown = config.agentBudget[field.key]
    if (typeof value !== 'number' || !Number.isFinite(value)) {
      config.agentBudget[field.key] = base[field.key]
    }
  }
  const tokenBudget: unknown = config.agentBudget.inputTokenBudget
  if (typeof tokenBudget !== 'number' || !Number.isFinite(tokenBudget)) {
    config.agentBudget.inputTokenBudget = base.inputTokenBudget
  }
}

/** 保存前校验：角色指令上限、复盘时刻/回看天数、预算正整数。 */
function validateDraft(): string | null {
  const config = draft.value
  if (!config) return null

  for (const key of ROLE_KEYS) {
    const length = (config.roleInstructions[key] ?? '').length
    if (length > ROLE_INSTRUCTION_MAX) {
      return `「${ROLE_LABELS[key]}」指令 ${length} 字，超出 ${ROLE_INSTRUCTION_MAX} 字上限，请精简后再保存`
    }
  }

  if (!LOCAL_TIME_PATTERN.test(config.review.localTime)) {
    return '复盘时刻格式应为 HH:mm（00:00–23:59）'
  }
  if (
    !Number.isInteger(config.review.maxLookbackDays) ||
    config.review.maxLookbackDays < 1 ||
    config.review.maxLookbackDays > 30
  ) {
    return '复盘回看天数需为 1–30 的整数'
  }

  for (const field of [...TIMEOUT_FIELDS, ...TOOL_LIMIT_FIELDS]) {
    const value = config.agentBudget[field.key]
    if (!Number.isInteger(value) || value < 1) {
      return `「${field.label}」需为大于 0 的整数`
    }
  }
  const tokenBudget = config.agentBudget.inputTokenBudget
  if (!Number.isInteger(tokenBudget) || tokenBudget < 1000 || tokenBudget > 150000) {
    return '输入 token 预算需为 1,000–150,000 的整数'
  }

  return null
}

/** 保存前收敛：角色指令去首尾空白。 */
function applyDraftNormalization() {
  const config = draft.value
  if (!config) return
  for (const key of ROLE_KEYS) {
    config.roleInstructions[key] = (config.roleInstructions[key] ?? '').trim()
  }
}

async function save() {
  if (!saved.value || !draft.value || saving.value) return
  applyBudgetFallbacks()
  const validationError = validateDraft()
  if (validationError) {
    Message.error(validationError)
    return
  }
  applyDraftNormalization()
  saving.value = true
  try {
    const updated = await updateStrategy(saved.value.id, {
      name: nameInput.value.trim() || '未命名策略',
      description: descriptionInput.value.trim() || null,
      config: clone(draft.value),
    })
    saved.value = updated
    draft.value = normalizeDraftConfig(updated.config)
    nameInput.value = updated.name
    descriptionInput.value = updated.description ?? ''
    replaceInList(updated)
    Message.success('策略已保存')
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '保存失败')
  } finally {
    saving.value = false
  }
}

function replaceInList(strategy: Strategy) {
  const index = strategies.value.findIndex((s) => s.id === strategy.id)
  if (index >= 0) strategies.value[index] = strategy
}

async function createNew() {
  if (!defaults.value) return
  try {
    const created = await createStrategy({
      name: '新策略',
      description: null,
      config: clone(defaults.value.config),
    })
    strategies.value = [created, ...strategies.value]
    selectStrategy(created)
    Message.success('策略已创建')
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '创建失败')
  }
}

async function activate(strategy: Strategy) {
  try {
    await activateStrategy(strategy.id)
    await load()
    Message.success('策略已激活')
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '激活失败')
  }
}

async function duplicate(strategy: Strategy) {
  try {
    const copy = await duplicateStrategy(strategy.id)
    strategies.value = [copy, ...strategies.value]
    selectStrategy(copy)
    Message.success('已复制策略')
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '复制失败')
  }
}

function remove(strategy: Strategy) {
  Modal.confirm({
    title: '确认删除',
    content: `确定删除策略「${strategy.name}」？删除后不可恢复。`,
    okText: '删除',
    cancelText: '取消',
    maskClosable: false,
    okButtonProps: { status: 'danger' },
    onOk: async () => {
      try {
        await deleteStrategy(strategy.id)
        Message.success('策略已删除')
        if (selectedId.value === strategy.id) clearSelection()
        strategies.value = strategies.value.filter((s) => s.id !== strategy.id)
      } catch (err) {
        Message.error(err instanceof Error ? err.message : '删除失败')
        return false // Arco：onOk 返回 false 时弹窗不关闭
      }
    },
  })
}

function exportStrategy(strategy: Strategy) {
  try {
    const config = clone(strategy.config)
    // 对齐设计稿「公开分享时自动剥离」：导出即剥离外部数据源请求头（可能含密钥）
    for (const source of config.indicators.externalDataSources) source.headers = []
    const payload = {
      exported_at: new Date().toISOString(),
      version: '1.0',
      name: strategy.name,
      description: strategy.description,
      config,
    }
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `strategy_${strategy.name}_${formatDate(new Date())}.json`
    link.click()
    URL.revokeObjectURL(url)
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '导出失败')
  }
}

function formatDate(date: Date): string {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

function importStrategy() {
  const input = document.createElement('input')
  input.type = 'file'
  input.accept = '.json,application/json'
  input.onchange = async () => {
    const file = input.files?.[0]
    if (!file) return
    try {
      const parsed = JSON.parse(await file.text()) as { name?: unknown; description?: unknown; config?: unknown }
      if (
        typeof parsed?.name !== 'string' ||
        parsed.name.trim() === '' ||
        !parsed.config ||
        typeof parsed.config !== 'object'
      ) {
        Message.error('无效的策略文件')
        return
      }
      const created = await createStrategy({
        name: `${parsed.name.trim()}（导入）`,
        description:
          typeof parsed.description === 'string' && parsed.description.trim() !== ''
            ? parsed.description.trim()
            : null,
        config: normalizeImportedConfig(parsed.config as Partial<StrategyConfig>),
      })
      strategies.value = [created, ...strategies.value]
      selectStrategy(created)
      Message.success('策略已导入')
    } catch {
      Message.error('无效的策略文件')
    }
  }
  input.click()
}

// 与当前默认配置合并，保证完整形状（手工改过或缺字段/含旧字段的导入文件也能用）。
// 显式逐项组装：旧文件的 strategyType/gridConfig/variant 等已删除字段不得进入请求体
// （ApiJson 对未映射字段 Disallow，会 400 validation_failed）。
function normalizeImportedConfig(raw: Partial<StrategyConfig>): StrategyConfig {
  const base = clone(defaults.value?.config as StrategyConfig)
  return {
    coinSource: { ...base.coinSource, ...raw.coinSource },
    indicators: {
      ...base.indicators,
      ...raw.indicators,
      klines: { ...base.indicators.klines, ...raw.indicators?.klines },
      externalDataSources: raw.indicators?.externalDataSources ?? base.indicators.externalDataSources,
    },
    riskControl: { ...base.riskControl, ...raw.riskControl },
    promptSections: { ...base.promptSections, ...raw.promptSections },
    customPrompt: raw.customPrompt ?? base.customPrompt,
    decisionMemoryEnabled: raw.decisionMemoryEnabled ?? base.decisionMemoryEnabled,
    newsEnabled: raw.newsEnabled ?? base.newsEnabled,
    roleInstructions: { ...base.roleInstructions, ...raw.roleInstructions },
    agentBudget: { ...base.agentBudget, ...raw.agentBudget },
    review: { ...base.review, ...raw.review },
  }
}

// —— 试运行（POST SSE；演练，不下单）——

const testDrawerVisible = ref(false)
const testProviders = ref<Provider[]>([])
const testProviderId = ref<number | null>(null)
const loadingProviders = ref(false)
const testStarted = ref(false)
const testRunning = ref(false)
const streamStatus = ref<ConnectionState>('closed')
const testError = ref<string | null>(null)
const testCapabilityMissing = ref(false)
const testWarnings = ref<string[]>([])
const testCycle = ref<CycleEventPayload | null>(null)
const testRunActive = ref(false)
const testProposal = ref<ProposalDto | null>(null)
const testVerdict = ref<VerdictDto | null>(null)
const testStageOrder = ref<string[]>([])
const testStages = ref<Record<string, StageDto>>({})
const testTools = ref<Record<string, ToolEventPayload[]>>({})
const testToolKeys = new Set<string>()
let disposeTestRun: (() => void) | null = null

const STREAM_STATUS_META: Record<
  ConnectionState,
  { type: 'info' | 'success' | 'warning' | 'error' | 'normal'; title: string }
> = {
  connecting: { type: 'info', title: '正在连接试运行流…' },
  open: { type: 'success', title: '已连接，事件实时到达' },
  reconnecting: { type: 'warning', title: '连接中断，正在重试…' },
  error: { type: 'error', title: '试运行连接失败' },
  closed: { type: 'normal', title: '试运行流已结束' },
}

const CYCLE_STATUS_META: Record<CycleStatus, { label: string; color: string }> = {
  running: { label: '运行中', color: 'arcoblue' },
  completed: { label: '已完成', color: 'green' },
  failed: { label: '失败', color: 'red' },
  skipped: { label: '跳过', color: 'gray' },
  aborted: { label: '已中止', color: 'orange' },
}

const streamStatusMeta = computed(() => STREAM_STATUS_META[streamStatus.value])

const testStageBlocks = computed(() =>
  testStageOrder.value.map((stageId) => ({
    stageId,
    stage: testStages.value[stageId] ?? null,
    tools: testTools.value[stageId] ?? [],
  })),
)

const selectedTestProvider = computed(
  () => testProviders.value.find((provider) => provider.id === testProviderId.value) ?? null,
)

const selectedProviderUnverified = computed(() => {
  const provider = selectedTestProvider.value
  if (!provider) return false
  return provider.toolCallingSupported !== true || provider.toolCallingVerifiedAt === null
})

function budgetDefault(key: keyof AgentBudgetConfig): string {
  const value = defaults.value?.config.agentBudget?.[key]
  return typeof value === 'number' ? String(value) : ''
}

function resetAgentBudget() {
  if (!draft.value || !defaults.value) return
  draft.value.agentBudget = clone(defaults.value.config.agentBudget)
  Message.info('已恢复后端默认预算')
}

function shortId(value: string): string {
  return value.length > 8 ? value.slice(0, 8) : value
}

function toolRowKey(tool: ToolEventPayload, index: number): string {
  return `${tool.stageId}-${tool.at}-${tool.tool}-${index}`
}

function ensureTestStage(stageId: string) {
  if (!testStageOrder.value.includes(stageId)) {
    testStageOrder.value = [...testStageOrder.value, stageId]
  }
}

function toStageDto(event: StageEventPayload): StageDto {
  return {
    id: event.stageId,
    role: event.role,
    status: event.status,
    elapsedMs: event.elapsedMs,
    inputTokens: event.inputTokens,
    outputTokens: event.outputTokens,
    toolCalls: event.toolCalls,
    summary: event.summary,
    error: event.error,
  }
}

function toProposalDto(event: ProposalDto): ProposalDto {
  return {
    id: event.id,
    action: event.action,
    symbol: event.symbol,
    side: event.side,
    confidence: event.confidence,
    entryPrice: event.entryPrice,
    stopLossPrice: event.stopLossPrice,
    takeProfitPrice: event.takeProfitPrice,
    positionPct: event.positionPct,
    leverage: event.leverage,
    thesis: event.thesis,
    invalidation: event.invalidation,
  }
}

function toVerdictDto(event: VerdictEventPayload): VerdictDto {
  return {
    id: event.id,
    decision: event.decision,
    reasons: event.reasons,
    maxPositionPct: event.maxPositionPct,
    maxLeverage: event.maxLeverage,
    entryZoneMin: event.entryZoneMin,
    entryZoneMax: event.entryZoneMax,
    allowMarket: event.allowMarket,
    validUntilUtc: event.validUntilUtc,
  }
}

function handleTestEvent(event: CycleEvent) {
  switch (event.type) {
    case 'state':
      // 种子帧：试运行开始/结束的 isRunning（连接横幅已表达，仅用于细节展示）
      testRunActive.value = event.isRunning
      break
    case 'cycle':
      testCycle.value = event
      if (event.status !== 'running') testRunActive.value = false
      break
    case 'stage': {
      ensureTestStage(event.stageId)
      const stage = toStageDto(event)
      testStages.value = { ...testStages.value, [stage.id]: stage }
      break
    }
    case 'proposal':
      testProposal.value = toProposalDto(event)
      break
    case 'verdict':
      testVerdict.value = toVerdictDto(event)
      break
    case 'tool': {
      // 复盘工具帧没有周期（cycleId=null），不属于本次演练
      if (event.cycleId === null) break
      ensureTestStage(event.stageId)
      const key = `${event.stageId}|${event.at}|${event.tool}|${event.elapsedMs}|${event.success}`
      if (testToolKeys.has(key)) break
      testToolKeys.add(key)
      const existing = testTools.value[event.stageId] ?? []
      if (existing.length < 50) {
        testTools.value = { ...testTools.value, [event.stageId]: [...existing, event] }
      }
      break
    }
    case 'ping':
      break
    default:
      // 演练不产生 order/equity/position-history（合成账户，API-06），收到也忽略
      break
  }
}

function handleTestStatus(state: ConnectionState) {
  streamStatus.value = state
  if (state === 'closed' || state === 'error') {
    testRunning.value = false
    testRunActive.value = false
  }
}

function handleTestError(error: Error) {
  if (error instanceof ApiRequestError) {
    testError.value = error.message
    if (error.code === 'provider_capability_missing') testCapabilityMissing.value = true
    return
  }
  if (error.message.includes('已跳过该帧')) {
    if (!testWarnings.value.includes(error.message)) {
      testWarnings.value = [...testWarnings.value, error.message]
    }
    return
  }
  testError.value = error.message
}

function resetTestView() {
  testStarted.value = false
  testRunning.value = false
  streamStatus.value = 'closed'
  testError.value = null
  testCapabilityMissing.value = false
  testWarnings.value = []
  testCycle.value = null
  testRunActive.value = false
  testProposal.value = null
  testVerdict.value = null
  testStageOrder.value = []
  testStages.value = {}
  testTools.value = {}
  testToolKeys.clear()
}

function stopTestRun() {
  disposeTestRun?.()
  disposeTestRun = null
  if (testRunning.value) {
    testRunning.value = false
    streamStatus.value = 'closed'
  }
}

function startTestRun() {
  const providerId = testProviderId.value
  if (!saved.value || providerId === null) return
  disposeTestRun?.()
  disposeTestRun = null
  resetTestView()
  testStarted.value = true
  testRunning.value = true
  streamStatus.value = 'connecting'
  const body: TestRunRequest = { strategyId: saved.value.id, providerId }
  disposeTestRun = openTestRunStream(body, handleTestEvent, {
    onStatus: handleTestStatus,
    onError: handleTestError,
  })
}

async function openTestDrawer() {
  testDrawerVisible.value = true
  if (testProviders.value.length > 0 || loadingProviders.value) return
  loadingProviders.value = true
  try {
    const list = await listProviders()
    testProviders.value = list
    if (testProviderId.value === null && list.length > 0) {
      const verified = list.find(
        (provider) => provider.toolCallingSupported === true && provider.toolCallingVerifiedAt !== null,
      )
      testProviderId.value = (verified ?? list[0]).id
    }
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '加载模型提供商失败')
  } finally {
    loadingProviders.value = false
  }
}

// 任何关闭路径（遮罩 / Esc / 页头按钮）都终止流，避免后台继续消费
watch(testDrawerVisible, (visible) => {
  if (!visible) stopTestRun()
})

onBeforeUnmount(() => {
  disposeTestRun?.()
  disposeTestRun = null
})

onMounted(load)
</script>

<template>
  <div class="studio">
    <div class="studio-header">
      <div class="header-icon"><IconStar /></div>
      <div class="header-text">
        <h1>策略工作室</h1>
        <span class="header-sub">可视化配置和测试交易策略</span>
      </div>
      <a-alert v-if="errorText" type="error" closable class="header-alert" @close="errorText = null">
        {{ errorText }}
      </a-alert>
    </div>

    <div class="studio-body">
      <aside class="studio-left">
        <StrategyListPanel
          :strategies="strategies"
          :selected-id="selectedId"
          @select="selectStrategy"
          @create="createNew"
          @import="importStrategy"
          @export="exportStrategy"
          @duplicate="duplicate"
          @remove="remove"
        />
      </aside>

      <section class="studio-main">
        <div v-if="loading" class="studio-center">
          <LuckyCatScene class="loading-cat" />
          <span class="loading-text">正在加载策略工作室…</span>
        </div>
        <div v-else-if="!draft || !defaults" class="studio-center">
          <div class="empty-icon"><IconStar /></div>
          <div class="empty-text">选择或创建策略</div>
        </div>
        <StrategyEditor
          v-else
          :key="selectedId ?? 0"
          :config="draft"
          :default-config="defaults.config"
          :name="nameInput"
          :description="descriptionInput"
          :is-active="saved?.isActive ?? false"
          :dirty="isDirty"
          :saving="saving"
          @update:name="nameInput = $event"
          @update:description="descriptionInput = $event"
          @save="save"
          @activate="saved && activate(saved)"
        />
      </section>

      <aside class="studio-right">
        <div v-if="draft" class="right-head">
          <a-tag color="gold" size="small">演练，不下单</a-tag>
          <a-button type="primary" size="small" @click="openTestDrawer">
            <template #icon><IconPlayArrow /></template>
            试运行
          </a-button>
        </div>

        <a-tabs
          v-if="draft && defaults"
          :key="selectedId ?? 0"
          class="right-tabs"
          :default-active-key="'roles'"
          size="small"
        >
          <a-tab-pane key="roles" title="角色指令">
            <RolePromptTabs :config="draft" :prompt-section-defaults="defaults.promptSections" />
          </a-tab-pane>

          <a-tab-pane key="budget" title="预算与复盘">
            <div class="settings-panel">
              <section class="settings-section">
                <div class="section-head">
                  <span class="section-title">智能体预算</span>
                  <a-button size="mini" @click="resetAgentBudget">恢复默认</a-button>
                </div>
                <div class="section-desc">
                  留空时使用后端默认值（占位符）；超时与工具上限需为正整数，缺失/非法值会阻止保存。
                </div>

                <div class="field-group-title">阶段超时（秒）</div>
                <div class="field-grid">
                  <div v-for="field in TIMEOUT_FIELDS" :key="field.key" class="field">
                    <span class="field-label">{{ field.label }}</span>
                    <a-input-number
                      v-model="draft.agentBudget[field.key]"
                      :min="1"
                      :precision="0"
                      allow-clear
                      :placeholder="budgetDefault(field.key)"
                      :style="{ width: '100%' }"
                    />
                  </div>
                </div>

                <div class="field-group-title">工具调用上限（次/阶段）</div>
                <div class="field-grid">
                  <div v-for="field in TOOL_LIMIT_FIELDS" :key="field.key" class="field">
                    <span class="field-label">{{ field.label }}</span>
                    <a-input-number
                      v-model="draft.agentBudget[field.key]"
                      :min="1"
                      :precision="0"
                      allow-clear
                      :placeholder="budgetDefault(field.key)"
                      :style="{ width: '100%' }"
                    />
                  </div>
                </div>

                <div class="field">
                  <span class="field-label">单周期输入 token 预算</span>
                  <a-input-number
                    v-model="draft.agentBudget.inputTokenBudget"
                    :min="1000"
                    :max="150000"
                    :step="1000"
                    :precision="0"
                    allow-clear
                    :placeholder="budgetDefault('inputTokenBudget')"
                    :style="{ width: '100%' }"
                  />
                  <span class="field-hint">1,000 – 150,000；实际预算还会受提供商上下文与输出上限约束</span>
                </div>
              </section>

              <section class="settings-section">
                <div class="section-head">
                  <span class="section-title">每日复盘</span>
                  <a-switch v-model="draft.review.enabled" />
                </div>
                <div class="section-desc">按服务器本地时区触发；关闭后只保留手动复盘（复盘页）。</div>
                <div class="field-grid">
                  <div class="field">
                    <span class="field-label">触发时刻（HH:mm）</span>
                    <a-input
                      v-model="draft.review.localTime"
                      maxlength="5"
                      placeholder="00:10"
                      :error="localTimeError !== ''"
                    />
                    <span v-if="localTimeError" class="field-error">{{ localTimeError }}</span>
                    <span v-else class="field-hint">24 小时制，例如 00:10</span>
                  </div>
                  <div class="field">
                    <span class="field-label">回看天数</span>
                    <a-input-number
                      v-model="draft.review.maxLookbackDays"
                      :min="1"
                      :max="30"
                      :precision="0"
                      :style="{ width: '100%' }"
                    />
                    <span class="field-hint">1 – 30 天（后端按 30 天上限收敛）</span>
                  </div>
                </div>
              </section>
            </div>
          </a-tab-pane>

          <a-tab-pane key="preview" title="Prompt 预览">
            <PromptPreviewPanel :config="draft" />
          </a-tab-pane>
        </a-tabs>
        <div v-else class="studio-center">
          <div class="empty-text">选择策略后可预览与测试</div>
        </div>
      </aside>
    </div>

    <a-drawer
      v-model:visible="testDrawerVisible"
      :width="760"
      title="试运行 · 演练，不下单"
      :footer="false"
      unmount-on-close
      @close="stopTestRun"
    >
      <div class="test-run">
        <div class="test-run-head">
          <div class="test-run-head-text">
            <div class="test-run-title">多智能体演练</div>
            <div class="test-run-sub">
              使用合成账户与真实模型走完整流程；执行阶段的下单/账户类请求会被拒绝，属于预期结果。
            </div>
          </div>
          <a-tag color="gold">演练，不下单</a-tag>
        </div>

        <a-alert type="info" title="合成账户约束（API-06）">
          演练不允许任何交易/账户写请求。执行阶段的工具拒绝会以红色 tool 失败帧（含 errorCode 与中文原因）呈现，不是运行故障。
        </a-alert>

        <div class="run-toolbar">
          <a-select
            :model-value="testProviderId ?? undefined"
            placeholder="选择模型提供商"
            size="small"
            :loading="loadingProviders"
            :disabled="testRunning"
            :style="{ flex: '1 1 0%', minWidth: '0' }"
            @update:model-value="(value) => (testProviderId = value === undefined ? null : Number(value))"
          >
            <a-option v-for="provider in testProviders" :key="provider.id" :value="provider.id">
              {{ provider.name }}（{{ provider.model }}）{{
                provider.toolCallingSupported === true && provider.toolCallingVerifiedAt !== null
                  ? ''
                  : ' · 未验证'
              }}
            </a-option>
          </a-select>
          <a-button
            v-if="!testRunning"
            type="primary"
            size="small"
            :disabled="testProviderId === null || loadingProviders"
            @click="startTestRun"
          >
            {{ testStarted ? '重新试运行' : '开始试运行' }}
          </a-button>
          <a-button v-else status="danger" size="small" @click="stopTestRun">中止</a-button>
        </div>

        <a-alert v-if="!loadingProviders && testProviders.length === 0" type="warning" title="暂无模型提供商">
          请先在「模型提供商」页创建并完成工具调用验证。
        </a-alert>
        <a-alert v-else-if="selectedProviderUnverified" type="warning" title="所选提供商未通过工具调用验证">
          试运行会返回 422 provider_capability_missing；请先在「模型提供商」页（/providers）完成验证再重试。
        </a-alert>
        <a-alert v-if="isDirty" type="warning">
          当前有未保存修改；试运行读取的是已保存的策略配置，请先保存后再试运行。
        </a-alert>

        <a-alert
          v-if="testStarted"
          :type="streamStatusMeta.type"
          :title="streamStatusMeta.title"
          banner
          class="stream-banner"
        >
          <span v-if="testCycle">
            周期 {{ shortId(testCycle.cycleId) }} · {{ CYCLE_STATUS_META[testCycle.status].label }}
            <template v-if="testRunActive"> · 演练运行中</template>
          </span>
          <span v-else-if="testRunning">等待周期事件…</span>
          <span v-else>未收到周期事件。</span>
        </a-alert>

        <a-alert v-if="testCapabilityMissing" type="error" banner title="提供商未通过工具调用验证">
          <div>{{ testError }}</div>
          <div class="alert-hint">请先在「模型提供商」页（/providers）完成工具调用验证，再回到此处重试。</div>
        </a-alert>
        <a-alert v-else-if="testError" type="error" banner :title="streamStatusMeta.title">
          {{ testError }}
        </a-alert>

        <a-alert v-if="testWarnings.length > 0" type="warning" title="个别事件帧被跳过">
          <div v-for="warning in testWarnings" :key="warning" class="warning-line">{{ warning }}</div>
        </a-alert>

        <template v-if="testStarted">
          <div v-if="testStageBlocks.length === 0" class="test-empty">
            <a-spin v-if="testRunning" />
            <span>{{ testRunning ? '等待智能体阶段事件…' : '试运行已结束，未收到阶段事件。' }}</span>
          </div>

          <div v-else class="test-flow">
            <div v-for="block in testStageBlocks" :key="block.stageId" class="stage-block">
              <StageCard v-if="block.stage" :stage="block.stage" />
              <a-card v-else size="small" class="stage-pending">
                <a-spin :size="14" />
                <span>阶段执行中…</span>
              </a-card>

              <div v-if="block.tools.length > 0" class="tool-list">
                <div
                  v-for="(tool, index) in block.tools"
                  :key="toolRowKey(tool, index)"
                  class="tool-row"
                >
                  <a-tag size="small" :color="tool.success ? 'gray' : 'red'">{{ tool.tool }}</a-tag>
                  <span class="tool-time">{{ tool.elapsedMs }}ms</span>
                  <span v-if="!tool.success" class="tool-error">
                    {{ tool.errorCode ? `${tool.errorCode} · ` : '' }}{{ tool.error ?? '调用失败' }}
                  </span>
                </div>
              </div>
            </div>

            <ProposalCard v-if="testProposal" :proposal="testProposal" />
            <VerdictCard v-if="testVerdict" :verdict="testVerdict" />
          </div>
        </template>
      </div>
    </a-drawer>
  </div>
</template>

<style scoped>
/* 三栏固定布局：页面不滚动，列内滚动（设计稿「视口高固定」；项目顶栏 56px） */
.studio {
  display: flex;
  flex-direction: column;
  height: calc(100vh - 56px);
  overflow: hidden;
  background: var(--color-bg-1);
}

.studio-header {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-shrink: 0;
  height: 56px;
  padding: 0 16px;
  background: var(--color-bg-2);
  border-bottom: 1px solid var(--color-border-2);
}

/* 品牌金块（设计稿头部条；本页唯一非 Arco 语义色的装饰元素） */
.header-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  border-radius: 8px;
  background: #f0b90b;
  color: #1d2129;
  font-size: 20px;
  flex-shrink: 0;
}

.header-text {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex: 1;
  min-width: 0;
}

.header-text h1 {
  margin: 0;
  font-size: 18px;
  font-weight: 700;
  color: var(--color-text-1);
  white-space: nowrap;
}

.header-sub {
  font-size: 12px;
  color: var(--color-text-3);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.header-alert {
  max-width: 480px;
  flex-shrink: 0;
}

.studio-body {
  display: flex;
  flex: 1;
  min-height: 0;
}

.studio-left {
  width: 192px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--color-bg-2);
  border-right: 1px solid var(--color-border-2);
}

.studio-main {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
  padding: 16px;
}

.studio-right {
  width: 420px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--color-bg-2);
  border-left: 1px solid var(--color-border-2);
}

.right-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 10px 12px 0;
  flex-shrink: 0;
}

.right-tabs {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  padding: 0 12px 12px;
}

.right-tabs :deep(.arco-tabs-content) {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.right-tabs :deep(.arco-tabs-nav) {
  padding: 0 2px;
}

.studio-center {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  height: 100%;
  color: var(--color-text-3);
}

.loading-cat {
  width: 130px;
  height: 130px;
}

.empty-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 56px;
  height: 56px;
  border: 1px solid var(--color-border-2);
  border-radius: 50%;
  font-size: 24px;
  opacity: 0.6;
}

.empty-text {
  font-size: 14px;
}

/* —— 预算 / 复盘表单 —— */

.settings-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.settings-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
}

.section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.section-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.section-desc {
  font-size: 11px;
  line-height: 1.5;
  color: var(--color-text-3);
}

.field-group-title {
  margin-top: 2px;
  font-size: 11px;
  font-weight: 600;
  color: var(--color-text-2);
}

.field-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px 10px;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.field-label {
  font-size: 11px;
  color: var(--color-text-3);
}

.field-error {
  font-size: 11px;
  line-height: 1.4;
  color: rgb(var(--danger-6));
}

.field-hint {
  font-size: 10px;
  line-height: 1.4;
  color: var(--color-text-3);
}

/* —— 试运行抽屉 —— */

.test-run {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.test-run-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.test-run-head-text {
  min-width: 0;
}

.test-run-title {
  font-size: 15px;
  font-weight: 700;
  color: var(--color-text-1);
}

.test-run-sub {
  margin-top: 2px;
  font-size: 12px;
  line-height: 1.6;
  color: var(--color-text-3);
}

.run-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
}

.alert-hint {
  margin-top: 4px;
  font-size: 12px;
}

.warning-line {
  font-size: 12px;
  line-height: 1.5;
}

.stream-banner {
  font-size: 12px;
}

.test-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 40px 16px;
  font-size: 12px;
  color: var(--color-text-3);
}

.test-flow {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.stage-block {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.stage-pending {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--color-text-3);
}

.tool-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 6px 8px;
  border: 1px dashed var(--color-border-2);
  border-radius: 6px;
}

.tool-row {
  display: flex;
  align-items: baseline;
  gap: 8px;
  min-width: 0;
}

.tool-time {
  flex-shrink: 0;
  font-size: 10px;
  color: var(--color-text-3);
  font-variant-numeric: tabular-nums;
}

.tool-error {
  font-size: 11px;
  line-height: 1.5;
  color: rgb(var(--danger-6));
  word-break: break-word;
}
</style>
