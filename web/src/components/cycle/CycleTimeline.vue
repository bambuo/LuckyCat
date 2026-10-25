<script setup lang="ts">
import { computed } from 'vue'
import type {
  CycleDto,
  CycleStatus,
  ExecutionMode,
  ProposalDto,
  StageDto,
  StageRole,
  TriggerType,
} from '../../types'
import StageCard from './StageCard.vue'

const props = defineProps<{
  cycle: CycleDto
  stages: StageDto[]
  proposal?: ProposalDto | null
}>()

const PIPELINE: StageRole[] = ['analyst', 'trader', 'risk', 'executor']

const ROLE_LABELS: Record<StageRole, string> = {
  analyst: '分析师',
  trader: '交易员',
  risk: '风控官',
  executor: '执行员',
  reviewer: '复盘员',
}

const TRIGGER_META: Record<TriggerType, { label: string; color: string }> = {
  schedule: { label: '定时', color: 'purple' },
  manual: { label: '手动', color: 'cyan' },
  test: { label: '试运行', color: 'gold' },
}

const MODE_LABELS: Record<ExecutionMode, string> = {
  dry: '演练',
  live: '实盘',
}

const STATUS_META: Record<CycleStatus, { label: string; color: string }> = {
  running: { label: '运行中', color: 'arcoblue' },
  completed: { label: '已完成', color: 'green' },
  failed: { label: '失败', color: 'red' },
  skipped: { label: '跳过', color: 'gray' },
  aborted: { label: '已中止', color: 'orange' },
}

type PlaceholderReason = 'running' | 'noRisk' | 'skipped'

const PLACEHOLDER_META: Record<PlaceholderReason, { label: string; color: string }> = {
  running: { label: '进行中', color: 'arcoblue' },
  noRisk: { label: '本路径不经过风控', color: 'gray' },
  skipped: { label: '本路径跳过', color: 'gray' },
}

interface TimelineNode {
  role: StageRole
  stage: StageDto | null
  reason: PlaceholderReason | null
}

const nodes = computed<TimelineNode[]>(() => {
  const byRole = new Map<StageRole, StageDto>()
  for (const stage of props.stages) {
    if (stage.role !== 'reviewer') byRole.set(stage.role, stage)
  }

  let lastPresent = -1
  PIPELINE.forEach((role, index) => {
    if (byRole.has(role)) lastPresent = index
  })

  return PIPELINE.map((role, index) => {
    const stage = byRole.get(role) ?? null
    if (stage) return { role, stage, reason: null }
    // 平仓/观望路径不经过风控：运行中也必须显示「本路径不经过风控」，而不是「进行中」
    if (role === 'risk' && (props.proposal?.action === 'close' || props.proposal?.action === 'hold')) {
      return { role, stage: null, reason: 'noRisk' }
    }
    if (props.cycle.status === 'running' && index > lastPresent) {
      return { role, stage: null, reason: 'running' }
    }
    return { role, stage: null, reason: 'skipped' }
  })
})

// 枚举数据来自 API：意外字符串也要有兜底，渲染不能因缺 key 抛错
function triggerMeta(value: TriggerType): { label: string; color: string } {
  return TRIGGER_META[value] ?? { label: value, color: 'gray' }
}

function modeLabel(value: ExecutionMode): string {
  return MODE_LABELS[value] ?? value
}

function statusMeta(value: CycleStatus): { label: string; color: string } {
  return STATUS_META[value] ?? { label: value, color: 'gray' }
}

function roleLabel(role: StageRole): string {
  return ROLE_LABELS[role] ?? role
}

function placeholderMeta(reason: PlaceholderReason | null): { label: string; color: string } {
  return PLACEHOLDER_META[reason ?? 'skipped'] ?? { label: reason ?? '—', color: 'gray' }
}

function dotColor(node: TimelineNode): string {
  if (node.stage) {
    if (node.stage.status === 'completed') return 'rgb(var(--success-6))'
    if (node.stage.status === 'failed') return 'rgb(var(--danger-6))'
    return 'rgb(var(--gray-5))'
  }
  return node.reason === 'running' ? 'rgb(var(--primary-6))' : 'rgb(var(--gray-5))'
}

function fmtTime(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString('zh-CN', { hour12: false })
}
</script>

<template>
  <a-card size="small" class="cycle-timeline">
    <template #title>
      <a-space :size="8">
        <span class="cycle-title">交易周期</span>
        <a-tag size="small" :color="triggerMeta(cycle.trigger).color">{{ triggerMeta(cycle.trigger).label }}</a-tag>
        <a-tag size="small" :color="cycle.mode === 'live' ? 'red' : 'arcoblue'">{{ modeLabel(cycle.mode) }}</a-tag>
        <a-tag size="small" :color="statusMeta(cycle.status).color">{{ statusMeta(cycle.status).label }}</a-tag>
      </a-space>
    </template>
    <template #extra>
      <span class="cycle-times">开始 {{ fmtTime(cycle.startedAt) }} · 结束 {{ fmtTime(cycle.completedAt) }}</span>
    </template>

    <a-alert v-if="cycle.errorCode || cycle.error" type="error" class="cycle-error">
      <span v-if="cycle.errorCode" class="cycle-error-code">{{ cycle.errorCode }}</span>
      <span v-if="cycle.error">{{ cycle.error }}</span>
    </a-alert>

    <div v-if="cycle.trigger === 'manual'" class="cycle-manual-note">
      手动平仓只调用执行工具，不经过分析师 / 交易员 / 风控阶段。
    </div>
    <a-timeline v-else class="cycle-timeline-list">
      <a-timeline-item v-for="node in nodes" :key="node.role" :dot-color="dotColor(node)">
        <StageCard v-if="node.stage" :stage="node.stage!" />
        <a-card v-else size="small" class="stage-placeholder">
          <a-space :size="8">
            <span class="placeholder-role">{{ roleLabel(node.role) }}</span>
            <a-tag size="small" :color="placeholderMeta(node.reason).color">{{ placeholderMeta(node.reason).label }}</a-tag>
            <span v-if="node.reason === 'running'" class="placeholder-hint">等待该阶段执行…</span>
          </a-space>
        </a-card>
      </a-timeline-item>
    </a-timeline>
  </a-card>
</template>

<style scoped>
.cycle-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.cycle-times {
  font-size: 12px;
  color: var(--color-text-3);
  font-variant-numeric: tabular-nums;
}

.cycle-error {
  margin-bottom: 12px;
}

.cycle-error-code {
  margin-right: 8px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
}

.cycle-timeline-list {
  padding-top: 2px;
}

.cycle-manual-note {
  font-size: 12px;
  color: var(--color-text-3);
}

.cycle-timeline-list :deep(.arco-timeline-item) {
  min-height: 0;
  padding-bottom: 10px;
}

.cycle-timeline-list :deep(.arco-timeline-item:last-child) {
  padding-bottom: 0;
}

.cycle-timeline-list :deep(.arco-timeline-item-content) {
  margin-bottom: 0;
}

.stage-placeholder {
  border-style: dashed;
  background: var(--color-fill-1);
}

.stage-placeholder :deep(.arco-card-body) {
  padding: 10px 12px;
}

.placeholder-role {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-2);
}

.placeholder-hint {
  font-size: 12px;
  color: var(--color-text-3);
}
</style>
