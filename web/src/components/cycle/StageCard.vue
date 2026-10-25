<script setup lang="ts">
import { ref } from 'vue'
import type { StageDto, StageRole, StageStatus } from '../../types'

defineProps<{ stage: StageDto }>()

const ROLE_LABELS: Record<StageRole, string> = {
  analyst: '分析师',
  trader: '交易员',
  risk: '风控官',
  executor: '执行员',
  reviewer: '复盘员',
}

const STATUS_META: Record<StageStatus, { label: string; color: string }> = {
  completed: { label: '完成', color: 'green' },
  failed: { label: '失败', color: 'red' },
  skipped: { label: '跳过', color: 'gray' },
}

const expanded = ref(true)

function formatElapsed(ms: number): string {
  return `${(ms / 1000).toFixed(1)}s`
}

function formatTokens(value: number | null): string {
  return value === null ? '—' : value.toLocaleString()
}
</script>

<template>
  <a-card size="small" class="stage-card">
    <template #title>
      <a-space :size="8">
        <span class="stage-role">{{ ROLE_LABELS[stage.role] }}</span>
        <a-tag size="small" :color="STATUS_META[stage.status].color">{{ STATUS_META[stage.status].label }}</a-tag>
        <span class="stage-elapsed">{{ formatElapsed(stage.elapsedMs) }}</span>
      </a-space>
    </template>
    <template #extra>
      <a-button type="text" size="mini" @click="expanded = !expanded">{{ expanded ? '收起' : '展开' }}</a-button>
    </template>

    <div v-if="!expanded" class="stage-collapsed">{{ stage.summary || '—' }}</div>
    <template v-else>
      <a-descriptions :column="3" size="small" class="stage-metrics">
        <a-descriptions-item label="耗时">{{ formatElapsed(stage.elapsedMs) }}</a-descriptions-item>
        <a-descriptions-item label="输入 token">{{ formatTokens(stage.inputTokens) }}</a-descriptions-item>
        <a-descriptions-item label="输出 token">{{ formatTokens(stage.outputTokens) }}</a-descriptions-item>
        <a-descriptions-item label="工具调用">{{ stage.toolCalls }} 次</a-descriptions-item>
      </a-descriptions>
      <div v-if="stage.summary" class="stage-summary">{{ stage.summary }}</div>
    </template>

    <a-alert v-if="stage.error" type="error" class="stage-error">{{ stage.error }}</a-alert>
  </a-card>
</template>

<style scoped>
.stage-role {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.stage-elapsed {
  font-size: 12px;
  color: var(--color-text-3);
  font-variant-numeric: tabular-nums;
}

.stage-metrics {
  margin-bottom: 8px;
}

.stage-metrics :deep(.arco-descriptions-item-label) {
  color: var(--color-text-3);
}

.stage-summary {
  font-size: 13px;
  line-height: 1.6;
  color: var(--color-text-2);
  white-space: pre-wrap;
  word-break: break-word;
}

.stage-collapsed {
  font-size: 12px;
  color: var(--color-text-3);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.stage-error {
  margin-top: 8px;
}
</style>
