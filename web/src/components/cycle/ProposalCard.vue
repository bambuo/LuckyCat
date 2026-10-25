<script setup lang="ts">
import type { ProposalAction, ProposalDto } from '../../types'

defineProps<{ proposal: ProposalDto }>()

const ACTION_META: Record<ProposalAction, { label: string; color: string }> = {
  open: { label: '开仓', color: 'arcoblue' },
  close: { label: '平仓', color: 'orange' },
  hold: { label: '观望', color: 'gray' },
}

function text(value: string | null): string {
  return value ?? '—'
}

function sideLabel(side: 'long' | 'short' | null): string {
  if (side === 'long') return '多'
  if (side === 'short') return '空'
  return '—'
}
</script>

<template>
  <a-card size="small" class="proposal-card">
    <template #title>
      <a-space :size="8">
        <span class="card-title">交易提案</span>
        <a-tag size="small" :color="ACTION_META[proposal.action].color">{{ ACTION_META[proposal.action].label }}</a-tag>
      </a-space>
    </template>
    <template #extra>
      <a-space :size="6">
        <span class="proposal-symbol">{{ text(proposal.symbol) }}</span>
        <a-tag
          v-if="proposal.side"
          size="small"
          :color="proposal.side === 'long' ? 'green' : 'red'"
        >{{ sideLabel(proposal.side) }}</a-tag>
        <span v-else class="proposal-muted">—</span>
      </a-space>
    </template>

    <a-descriptions :column="3" size="small" class="proposal-metrics">
      <a-descriptions-item label="置信度">{{ text(proposal.confidence) }}</a-descriptions-item>
      <a-descriptions-item label="入场价">{{ text(proposal.entryPrice) }}</a-descriptions-item>
      <a-descriptions-item label="止损价">{{ text(proposal.stopLossPrice) }}</a-descriptions-item>
      <a-descriptions-item label="止盈价">{{ text(proposal.takeProfitPrice) }}</a-descriptions-item>
      <a-descriptions-item label="仓位占比">{{ text(proposal.positionPct) }}</a-descriptions-item>
      <a-descriptions-item label="杠杆">{{ text(proposal.leverage) }}</a-descriptions-item>
    </a-descriptions>

    <div class="proposal-block">
      <div class="block-label">交易逻辑</div>
      <div class="block-text">{{ proposal.thesis || '—' }}</div>
    </div>

    <div v-if="proposal.invalidation" class="proposal-block">
      <div class="block-label">失效条件</div>
      <div class="block-text">{{ proposal.invalidation }}</div>
    </div>
  </a-card>
</template>

<style scoped>
.card-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.proposal-symbol {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.proposal-muted {
  font-size: 13px;
  color: var(--color-text-3);
}

.proposal-metrics {
  margin-bottom: 12px;
}

.proposal-metrics :deep(.arco-descriptions-item-label) {
  color: var(--color-text-3);
}

.proposal-metrics :deep(.arco-descriptions-item-value) {
  font-variant-numeric: tabular-nums;
}

.proposal-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.proposal-block + .proposal-block {
  margin-top: 10px;
  padding-top: 10px;
  border-top: 1px solid var(--color-border-1);
}

.block-label {
  font-size: 11px;
  letter-spacing: 0.08em;
  color: var(--color-text-3);
}

.block-text {
  font-size: 13px;
  line-height: 1.6;
  color: var(--color-text-2);
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
