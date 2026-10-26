<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { IconHistory, IconRefresh } from '@arco-design/web-vue/es/icon'
import { getCycle, getCycleStage, listTraderCycles, listTraders } from '../api'
import CycleTimeline from '../components/cycle/CycleTimeline.vue'
import OrderCard from '../components/cycle/OrderCard.vue'
import ProposalCard from '../components/cycle/ProposalCard.vue'
import VerdictCard from '../components/cycle/VerdictCard.vue'
import type {
  CycleDetailDto,
  CycleDto,
  CycleStatus,
  ExecutionMode,
  StageDetailDto,
  StageDto,
  StageRole,
  StageStatus,
  Trader,
  TriggerType,
} from '../types'

const route = useRoute()
const router = useRouter()

const PAGE_SIZE_OPTIONS = [10, 20, 50]
const ERROR_TEXT_MAX = 36

// 展示映射与 CycleTimeline 保持一致（组件未导出常量，故页面侧声明同一口径）。
const TRIGGER_META: Record<TriggerType, { label: string; color: string }> = {
  schedule: { label: '定时', color: 'purple' },
  manual: { label: '手动', color: 'cyan' },
  test: { label: '试运行', color: 'gold' },
}

const MODE_META: Record<ExecutionMode, { label: string; color: string }> = {
  dry: { label: '演练', color: 'arcoblue' },
  live: { label: '实盘', color: 'red' },
}

const CYCLE_STATUS_META: Record<CycleStatus, { label: string; color: string }> = {
  running: { label: '运行中', color: 'arcoblue' },
  completed: { label: '已完成', color: 'green' },
  failed: { label: '失败', color: 'red' },
  skipped: { label: '跳过', color: 'gray' },
  aborted: { label: '已中止', color: 'orange' },
}

const STAGE_STATUS_META: Record<StageStatus, { label: string; color: string }> = {
  completed: { label: '完成', color: 'green' },
  failed: { label: '失败', color: 'red' },
  skipped: { label: '跳过', color: 'gray' },
}

const ROLE_LABELS: Record<StageRole, string> = {
  analyst: '分析师',
  trader: '交易员',
  risk: '风控官',
  executor: '执行员',
  reviewer: '复盘员',
}

const TRANSCRIPT_ROLE_LABELS: Record<string, string> = {
  assistant: '模型',
  user: '用户',
  tool: '工具',
  system: '系统',
}

// —— 交易员选择（?trader=<id>，与看板同一约定） ——

const traders = ref<Trader[]>([])
// 首次加载前即为加载态：避免空数组先闪一下「暂无交易员/暂无周期」
const tradersLoading = ref(true)
const selectedId = computed<number | null>(() => {
  const raw = route.query.trader
  const id = Number(Array.isArray(raw) ? raw[0] : raw)
  return Number.isFinite(id) && id > 0 ? id : null
})

function onSwitch(value: unknown) {
  const id = Number(value)
  if (!Number.isFinite(id) || id <= 0 || id === selectedId.value) return
  void router.replace({ path: '/cycles', query: { trader: String(id) } })
}

async function loadTraders() {
  tradersLoading.value = true
  try {
    traders.value = await listTraders()
    if (traders.value.length === 0) return
    const current = selectedId.value
    if (current === null || !traders.value.some((t) => t.id === current)) {
      await router.replace({ path: '/cycles', query: { trader: String(traders.value[0].id) } })
    }
  } catch (err) {
    errorText.value = err instanceof Error ? err.message : '加载交易员失败'
    // 没有交易员就无法发起列表请求：结束初始加载态，避免 spinner 永久旋转
    listLoading.value = false
  } finally {
    tradersLoading.value = false
  }
}

// —— 周期列表（page 从 1 起；后端 page>10000 直接 400，原文透出） ——

const cycles = ref<CycleDto[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const listLoading = ref(true)
const errorText = ref<string | null>(null)
let listSeq = 0

async function loadCycles() {
  const traderId = selectedId.value
  if (traderId === null) return
  const seq = ++listSeq
  listLoading.value = true
  try {
    const paged = await listTraderCycles(traderId, page.value, pageSize.value)
    if (seq !== listSeq) return
    cycles.value = paged.items
    total.value = paged.total
    if (paged.page !== page.value) page.value = paged.page
    errorText.value = null
  } catch (err) {
    if (seq !== listSeq) return
    errorText.value = err instanceof Error ? err.message : '加载周期失败'
  } finally {
    if (seq === listSeq) listLoading.value = false
  }
}

function onPageChange(next: number) {
  if (next === page.value) return
  page.value = next
  void loadCycles()
}

function onPageSizeChange(size: number) {
  if (size === pageSize.value) return
  pageSize.value = size
  page.value = 1
  void loadCycles()
}

function refreshList() {
  void loadCycles()
}

// —— 周期详情（抽屉） ——

const drawerVisible = ref(false)
const detail = ref<CycleDetailDto | null>(null)
const detailLoading = ref(false)
const detailError = ref<string | null>(null)
let detailSeq = 0

const stageDetails = ref<Record<string, StageDetailDto>>({})
const stageLoading = ref<Record<string, boolean>>({})
const stageErrors = ref<Record<string, string>>({})
const expandedStages = ref<Record<string, boolean>>({})

interface TranscriptEntry {
  role: string
  text: string
  tools: string[]
}

interface StageView {
  stage: StageDto
  expanded: boolean
  loading: boolean
  error: string | null
  detail: StageDetailDto | null
  entries: TranscriptEntry[] | null
}

/** 阶段记录正文是存储的 JSON 字符串（[{role,text,tools}]）；解析失败返回 null，由界面展示原文。 */
function parseTranscript(raw: string): TranscriptEntry[] | null {
  if (!raw.trim()) return []
  try {
    const value: unknown = JSON.parse(raw)
    if (!Array.isArray(value)) return null
    const items: unknown[] = value
    const entries: TranscriptEntry[] = []
    for (const item of items) {
      if (item === null || typeof item !== 'object') continue
      const record = item as Record<string, unknown>
      entries.push({
        role: typeof record.role === 'string' ? record.role : 'unknown',
        text: typeof record.text === 'string' ? record.text : '',
        tools: Array.isArray(record.tools)
          ? record.tools.filter((tool): tool is string => typeof tool === 'string')
          : [],
      })
    }
    return entries
  } catch {
    return null
  }
}

const stageViews = computed<StageView[]>(() =>
  (detail.value?.stages ?? []).map((stage) => {
    const loaded = stageDetails.value[stage.id] ?? null
    return {
      stage,
      expanded: expandedStages.value[stage.id] === true,
      loading: stageLoading.value[stage.id] === true,
      error: stageErrors.value[stage.id] ?? null,
      detail: loaded,
      entries: loaded ? parseTranscript(loaded.transcriptJson) : null,
    }
  }),
)

function resetStageState() {
  stageDetails.value = {}
  stageLoading.value = {}
  stageErrors.value = {}
  expandedStages.value = {}
}

async function openCycle(cycle: CycleDto) {
  detailSeq++
  drawerVisible.value = true
  detail.value = null
  detailError.value = null
  detailLoading.value = true
  resetStageState()
  const seq = detailSeq
  try {
    const fetched = await getCycle(cycle.id)
    if (seq !== detailSeq) return
    detail.value = fetched
  } catch (err) {
    if (seq === detailSeq) detailError.value = err instanceof Error ? err.message : '加载周期详情失败'
  } finally {
    if (seq === detailSeq) detailLoading.value = false
  }
}

function closeDrawer() {
  drawerVisible.value = false
  detailSeq++
  detailLoading.value = false
  detail.value = null
  detailError.value = null
  resetStageState()
}

function toggleStage(view: StageView) {
  const nextExpanded = !view.expanded
  expandedStages.value = { ...expandedStages.value, [view.stage.id]: nextExpanded }
  if (nextExpanded && view.detail === null && !view.loading) void loadStage(view.stage.id)
}

async function loadStage(stageId: string) {
  const cycleId = detail.value?.cycle.id
  if (!cycleId) return
  stageLoading.value = { ...stageLoading.value, [stageId]: true }
  const nextErrors = { ...stageErrors.value }
  delete nextErrors[stageId]
  stageErrors.value = nextErrors
  try {
    const fetched = await getCycleStage(cycleId, stageId)
    if (detail.value?.cycle.id !== cycleId) return
    stageDetails.value = { ...stageDetails.value, [stageId]: fetched }
  } catch (err) {
    if (detail.value?.cycle.id !== cycleId) return
    stageErrors.value = {
      ...stageErrors.value,
      [stageId]: err instanceof Error ? err.message : '加载阶段记录失败',
    }
  } finally {
    if (detail.value?.cycle.id === cycleId) {
      stageLoading.value = { ...stageLoading.value, [stageId]: false }
    }
  }
}

// —— 展示辅助 ——

function fmtTime(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString('zh-CN', { hour12: false })
}

function formatElapsed(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms) || ms < 0) return '—'
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.floor(ms / 60000)}m${Math.round((ms % 60000) / 1000)}s`
}

function shortError(value: string | null): string {
  if (!value) return '—'
  return value.length > ERROR_TEXT_MAX ? `${value.slice(0, ERROR_TEXT_MAX)}…` : value
}

function transcriptRoleLabel(role: string): string {
  return TRANSCRIPT_ROLE_LABELS[role] ?? role
}

function formatToken(value: number | null): string {
  return value === null ? '—' : value.toLocaleString()
}

onMounted(async () => {
  const before = selectedId.value
  await loadTraders()
  // 查询参数被纠正（缺省/失效）时由 selectedId 的 watch 统一拉取，避免重复请求
  if (selectedId.value !== before) return
  if (traders.value.length > 0) await loadCycles()
})

watch(selectedId, (id, previous) => {
  if (id === previous) return
  page.value = 1
  cycles.value = []
  total.value = 0
  errorText.value = null
  closeDrawer()
  if (id !== null) void loadCycles()
})
</script>

<template>
  <div class="page-content">
    <div class="page-header">
      <div class="header-icon"><IconHistory /></div>
      <div class="header-text">
        <h1>周期历史</h1>
        <a-tag color="gold" size="small">{{ total }} 个周期</a-tag>
      </div>
      <div class="header-actions">
        <span class="trader-label">交易员</span>
        <a-select
          :model-value="selectedId ?? undefined"
          :loading="tradersLoading"
          :style="{ width: '220px' }"
          size="small"
          placeholder="选择交易员"
          @update:model-value="onSwitch"
        >
          <a-option v-for="t in traders" :key="t.id" :value="t.id">{{ t.name }}</a-option>
        </a-select>
        <a-button size="small" :loading="listLoading" @click="refreshList">
          <template #icon><IconRefresh /></template>
          刷新
        </a-button>
      </div>
    </div>

    <a-alert v-if="errorText" type="error" class="page-alert" closable @close="errorText = null">
      {{ errorText }}
    </a-alert>

    <div v-if="!tradersLoading && traders.length === 0 && !errorText" class="page-empty">
      <a-empty>
        <div class="empty-desc">暂无交易员</div>
        <a-button type="primary" @click="router.push('/traders')">去创建交易员</a-button>
      </a-empty>
    </div>

    <a-card v-else class="list-card">
      <template #title>周期列表</template>
      <template #extra>
        <span class="card-extra">共 {{ total }} 条 · 第 {{ page }} 页 · 每页 {{ pageSize }} 条</span>
      </template>

      <a-spin :loading="listLoading" class="list-spin">
        <div v-if="cycles.length === 0 && !listLoading && !errorText" class="list-empty">
          <a-empty description="暂无周期记录（交易员启动后按计划生成周期）" />
        </div>
        <table v-else class="data-table">
          <thead>
            <tr>
              <th>开始时间</th>
              <th>触发</th>
              <th>模式</th>
              <th>状态</th>
              <th class="num">耗时</th>
              <th>错误码</th>
              <th>错误信息</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="cycle in cycles"
              :key="cycle.id"
              class="cycle-row"
              :class="{ 'cycle-row-running': cycle.status === 'running' }"
              tabindex="0"
              title="点击查看周期详情"
              @click="openCycle(cycle)"
              @keydown.enter="openCycle(cycle)"
            >
              <td class="time-cell">{{ fmtTime(cycle.startedAt) }}</td>
              <td>
                <a-tag size="small" :color="TRIGGER_META[cycle.trigger].color">
                  {{ TRIGGER_META[cycle.trigger].label }}
                </a-tag>
              </td>
              <td>
                <a-tag size="small" :color="MODE_META[cycle.mode].color">
                  {{ MODE_META[cycle.mode].label }}
                </a-tag>
              </td>
              <td>
                <a-space :size="4">
                  <span v-if="cycle.status === 'running'" class="running-dot" aria-hidden="true"></span>
                  <a-tag size="small" :color="CYCLE_STATUS_META[cycle.status].color">
                    {{ CYCLE_STATUS_META[cycle.status].label }}
                  </a-tag>
                </a-space>
              </td>
              <td class="num">{{ formatElapsed(cycle.elapsedMs) }}</td>
              <td>
                <code v-if="cycle.errorCode" class="error-code">{{ cycle.errorCode }}</code>
                <span v-else class="muted">—</span>
              </td>
              <td class="error-cell" :title="cycle.error ?? ''">{{ shortError(cycle.error) }}</td>
            </tr>
          </tbody>
        </table>

        <div v-if="total > 0" class="table-footer">
          <a-pagination
            :current="page"
            :total="total"
            :page-size="pageSize"
            :page-size-options="PAGE_SIZE_OPTIONS"
            size="small"
            show-total
            show-page-size
            @change="onPageChange"
            @page-size-change="onPageSizeChange"
          />
        </div>
      </a-spin>
    </a-card>

    <a-drawer
      v-model:visible="drawerVisible"
      :width="820"
      :footer="false"
      title="周期详情"
      @close="closeDrawer"
    >
      <a-spin :loading="detailLoading" class="drawer-spin">
        <a-alert v-if="detailError" type="error" class="page-alert">{{ detailError }}</a-alert>

        <template v-if="detail">
          <CycleTimeline
            :cycle="detail.cycle"
            :stages="detail.stages"
            :proposal="detail.proposal"
          />

          <div v-if="detail.proposal || detail.verdict || detail.orders.length > 0" class="detail-cards">
            <ProposalCard v-if="detail.proposal" :proposal="detail.proposal" />
            <VerdictCard v-if="detail.verdict" :verdict="detail.verdict" />
            <OrderCard v-for="order in detail.orders" :key="order.orderId" :order="order" />
          </div>

          <a-card v-if="detail.stages.length > 0" size="small" class="transcript-card">
            <template #title>阶段全文</template>
            <template #extra>
              <span class="card-extra">按需加载 · 记录来自 stage transcript</span>
            </template>

            <div v-for="view in stageViews" :key="view.stage.id" class="stage-block">
              <div class="stage-head" @click="toggleStage(view)">
                <a-space :size="8" wrap>
                  <span class="stage-role">{{ ROLE_LABELS[view.stage.role] }}</span>
                  <a-tag size="small" :color="STAGE_STATUS_META[view.stage.status].color">
                    {{ STAGE_STATUS_META[view.stage.status].label }}
                  </a-tag>
                  <span class="stage-meta">
                    {{ formatElapsed(view.stage.elapsedMs) }} · 工具 {{ view.stage.toolCalls }} 次
                  </span>
                </a-space>
                <a-button type="text" size="mini">{{ view.expanded ? '收起' : '查看记录' }}</a-button>
              </div>

              <div v-if="view.expanded" class="stage-body">
                <div v-if="view.loading" class="stage-loading">
                  <a-spin :size="14" /> 加载阶段记录…
                </div>
                <a-alert v-else-if="view.error" type="error" class="stage-error">{{ view.error }}</a-alert>
                <template v-else-if="view.detail">
                  <div class="transcript-meta">
                    <a-tag v-if="view.detail.truncated" color="orange">已截断</a-tag>
                    <span v-if="view.detail.truncated" class="truncated-hint">
                      阶段记录超过上限，后端仅保留前段文本
                    </span>
                    <span class="transcript-model">
                      {{ view.detail.providerName }} / {{ view.detail.model }}
                    </span>
                    <span class="transcript-tokens">
                      token {{ formatToken(view.detail.inputTokens) }} → {{ formatToken(view.detail.outputTokens) }}
                    </span>
                  </div>

                  <a-alert v-if="view.detail.error" type="error" class="stage-error">
                    {{ view.detail.error }}
                  </a-alert>

                  <template v-if="view.entries && view.entries.length > 0">
                    <div v-for="(entry, index) in view.entries" :key="index" class="transcript-entry">
                      <div class="transcript-role">
                        <span>{{ transcriptRoleLabel(entry.role) }}</span>
                        <a-tag v-for="tool in entry.tools" :key="tool" size="small" color="gray">
                          {{ tool }}
                        </a-tag>
                      </div>
                      <div v-if="entry.text" class="transcript-text">{{ entry.text }}</div>
                    </div>
                  </template>
                  <template v-else-if="view.entries === null">
                    <div class="transcript-note">原始记录（无法解析为对话）</div>
                    <pre class="transcript-raw">{{ view.detail.transcriptJson }}</pre>
                  </template>
                  <div v-else class="transcript-note">该阶段没有文本记录</div>
                </template>
              </div>
            </div>
          </a-card>
        </template>
      </a-spin>
    </a-drawer>
  </div>
</template>

<style scoped>
.page-content {
  width: 100%;
  max-width: 1200px;
  margin: 0 auto;
  padding: 24px;
}

.page-header {
  display: flex;
  align-items: center;
  gap: 16px;
  padding-bottom: 20px;
  margin-bottom: 24px;
  border-bottom: 1px solid var(--color-border-2);
}

.header-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 52px;
  height: 52px;
  border: 1px solid rgba(240, 185, 11, 0.3);
  border-radius: 12px;
  background: var(--color-bg-2);
  color: rgb(var(--primary-6));
  font-size: 26px;
  flex-shrink: 0;
}

.header-text {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: 1;
  min-width: 0;
}

.header-text h1 {
  margin: 0;
  font-size: 24px;
  font-weight: 600;
  color: var(--color-text-1);
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.trader-label {
  font-size: 12px;
  color: var(--color-text-3);
}

.page-alert {
  margin-bottom: 16px;
}

.page-empty {
  padding: 72px 0;
  text-align: center;
  background: var(--color-bg-2);
  border-radius: 12px;
}

.empty-desc {
  margin-bottom: 12px;
  font-size: 13px;
  color: var(--color-text-3);
}

.list-card {
  border-radius: 12px;
}

.card-extra {
  font-size: 12px;
  color: var(--color-text-3);
}

.list-spin {
  display: block;
  width: 100%;
  min-height: 120px;
}

.list-empty {
  padding: 40px 0;
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
  font-variant-numeric: tabular-nums;
}

.time-cell {
  font-variant-numeric: tabular-nums;
}

.cycle-row {
  cursor: pointer;
  transition: background 0.15s ease;
}

.cycle-row:hover td {
  background: var(--color-fill-1);
}

.cycle-row:focus-visible {
  outline: 2px solid rgb(var(--primary-6));
  outline-offset: -2px;
}

.cycle-row-running td {
  background: var(--color-primary-light-1);
}

.cycle-row-running td:first-child {
  box-shadow: inset 3px 0 0 rgb(var(--primary-6));
}

.running-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: rgb(var(--primary-6));
  box-shadow: 0 0 0 3px rgba(var(--primary-6), 0.15);
  animation: pulse 1.6s ease-in-out infinite;
}

@keyframes pulse {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0.35;
  }
}

.error-code {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 11px;
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--color-danger-light-1);
  color: rgb(var(--danger-6));
}

.muted {
  color: var(--color-text-3);
}

.error-cell {
  max-width: 260px;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--color-text-2);
}

.table-footer {
  display: flex;
  justify-content: flex-end;
  padding-top: 16px;
}

.drawer-spin {
  display: block;
  width: 100%;
  min-height: 200px;
}

.detail-cards {
  display: flex;
  flex-direction: column;
  gap: 12px;
  margin-top: 12px;
}

.transcript-card {
  margin-top: 12px;
}

.stage-block {
  border-bottom: 1px solid var(--color-border-1);
}

.stage-block:last-child {
  border-bottom: none;
}

.stage-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 0;
  cursor: pointer;
}

.stage-role {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.stage-meta {
  font-size: 12px;
  color: var(--color-text-3);
  font-variant-numeric: tabular-nums;
}

.stage-body {
  padding: 0 0 12px;
}

.stage-loading {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--color-text-3);
  padding: 8px 0;
}

.stage-error {
  margin-bottom: 8px;
}

.transcript-meta {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  font-size: 12px;
  color: var(--color-text-3);
  margin-bottom: 8px;
}

.truncated-hint {
  color: rgb(var(--warning-6));
}

.transcript-model,
.transcript-tokens {
  font-variant-numeric: tabular-nums;
}

.transcript-entry {
  margin-bottom: 8px;
}

.transcript-role {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px;
  font-size: 11px;
  font-weight: 600;
  color: var(--color-text-3);
  margin-bottom: 2px;
}

.transcript-text {
  font-size: 12px;
  line-height: 1.65;
  color: var(--color-text-2);
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 360px;
  overflow-y: auto;
  padding-left: 8px;
  border-left: 2px solid var(--color-border-2);
}

.transcript-note {
  font-size: 12px;
  color: var(--color-text-3);
  padding: 4px 0;
}

.transcript-raw {
  margin: 0;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 11px;
  line-height: 1.6;
  color: var(--color-text-2);
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 360px;
  overflow: auto;
  padding: 8px;
  border-radius: 6px;
  background: var(--color-fill-1);
}

@media (max-width: 900px) {
  .page-header {
    flex-wrap: wrap;
  }

  .header-actions {
    width: 100%;
    justify-content: flex-end;
  }

  .error-cell {
    max-width: 160px;
  }
}
</style>
