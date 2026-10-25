<script setup lang="ts">
import type { RiskDecision, VerdictDto } from '../../types'

defineProps<{ verdict: VerdictDto }>()

const DECISION_META: Record<RiskDecision, { label: string; color: string }> = {
  approved: { label: '批准', color: 'green' },
  rejected: { label: '拒绝', color: 'red' },
}

function text(value: string | null): string {
  return value ?? '—'
}

function entryZoneText(verdict: VerdictDto): string {
  if (verdict.entryZoneMin === null && verdict.entryZoneMax === null) return '—'
  return `${verdict.entryZoneMin ?? '—'} ~ ${verdict.entryZoneMax ?? '—'}`
}

function allowMarketText(allowMarket: boolean | null): string {
  if (allowMarket === null) return '—'
  return allowMarket ? '允许' : '不允许'
}

function fmtTime(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString('zh-CN', { hour12: false })
}
</script>

<template>
  <a-card size="small" class="verdict-card">
    <template #title>
      <a-space :size="8">
        <span class="card-title">风控裁决</span>
        <a-tag size="small" :color="DECISION_META[verdict.decision].color">
          {{ DECISION_META[verdict.decision].label }}
        </a-tag>
      </a-space>
    </template>

    <a-descriptions :column="3" size="small" class="verdict-bounds">
      <a-descriptions-item label="仓位上限">{{ text(verdict.maxPositionPct) }}</a-descriptions-item>
      <a-descriptions-item label="杠杆上限">{{ text(verdict.maxLeverage) }}</a-descriptions-item>
      <a-descriptions-item label="入场区间">{{ entryZoneText(verdict) }}</a-descriptions-item>
      <a-descriptions-item label="市价单">{{ allowMarketText(verdict.allowMarket) }}</a-descriptions-item>
      <a-descriptions-item label="有效期至" :span="2">{{ fmtTime(verdict.validUntilUtc) }}</a-descriptions-item>
    </a-descriptions>

    <div class="verdict-reasons">
      <div class="reason-label">裁决理由</div>
      <ul v-if="verdict.reasons.length > 0" class="reason-list">
        <li v-for="(reason, index) in verdict.reasons" :key="index">{{ reason }}</li>
      </ul>
      <div v-else class="reason-empty">—</div>
    </div>
  </a-card>
</template>

<style scoped>
.card-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.verdict-bounds {
  margin-bottom: 12px;
}

.verdict-bounds :deep(.arco-descriptions-item-label) {
  color: var(--color-text-3);
}

.verdict-bounds :deep(.arco-descriptions-item-value) {
  font-variant-numeric: tabular-nums;
}

.verdict-reasons {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.reason-label {
  font-size: 11px;
  letter-spacing: 0.08em;
  color: var(--color-text-3);
}

.reason-list {
  margin: 0;
  padding-left: 18px;
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 13px;
  line-height: 1.6;
  color: var(--color-text-2);
}

.reason-list li {
  word-break: break-word;
}

.reason-empty {
  font-size: 13px;
  color: var(--color-text-3);
}
</style>
