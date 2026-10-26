<script setup lang="ts">
import type { CycleOrderDto, OrderStatus } from '../../types'

defineProps<{ order: CycleOrderDto }>()

const STATUS_META: Record<OrderStatus, { label: string; color: string }> = {
  submitted: { label: '已提交', color: 'arcoblue' },
  filled: { label: '已成交', color: 'green' },
  partial: { label: '部分成交', color: 'gold' },
  failed: { label: '失败', color: 'red' },
  unknown: { label: '结果未知', color: 'orange' },
  cancelled: { label: '已撤销', color: 'gray' },
}

const ACTION_META: Record<string, { label: string; color: string }> = {
  open: { label: '开仓', color: 'arcoblue' },
  close: { label: '平仓', color: 'orange' },
  buy: { label: '买入', color: 'arcoblue' },
  sell: { label: '卖出', color: 'orange' },
}

const SIDE_LABELS: Record<string, string> = {
  long: '多',
  short: '空',
  buy: '买入',
  sell: '卖出',
}

function statusMeta(status: OrderStatus | null): { label: string; color: string } {
  return status ? STATUS_META[status] : { label: '—', color: 'gray' }
}

function actionMeta(action: string): { label: string; color: string } {
  return ACTION_META[action] ?? { label: action, color: 'gray' }
}

function text(value: string | null): string {
  return value ?? '—'
}

function sideLabel(side: string | null): string {
  if (side === null) return '—'
  return SIDE_LABELS[side] ?? side
}

function sideTagColor(side: string | null): string {
  if (side === 'long' || side === 'buy') return 'green'
  if (side === 'short' || side === 'sell') return 'red'
  return 'gray'
}

function fmtTime(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString('zh-CN', { hour12: false })
}
</script>

<template>
  <a-card size="small" class="order-card">
    <template #title>
      <a-space :size="8">
        <span class="card-title">订单 #{{ order.orderId }}</span>
        <a-tag size="small" :color="actionMeta(order.action).color">{{ actionMeta(order.action).label }}</a-tag>
        <a-tag v-if="order.simulated" size="small" color="gold">模拟</a-tag>
      </a-space>
    </template>
    <template #extra>
      <a-space :size="6">
        <span class="order-symbol">{{ text(order.symbol) }}</span>
        <a-tag v-if="order.side" size="small" :color="sideTagColor(order.side)">{{ sideLabel(order.side) }}</a-tag>
        <a-tag size="small" :color="statusMeta(order.status).color">{{ statusMeta(order.status).label }}</a-tag>
      </a-space>
    </template>

    <a-descriptions :column="3" size="small" class="order-metrics">
      <a-descriptions-item label="数量">{{ text(order.quantity) }}</a-descriptions-item>
      <a-descriptions-item label="成交数量">{{ text(order.filledQuantity) }}</a-descriptions-item>
      <a-descriptions-item label="价格">{{ text(order.price) }}</a-descriptions-item>
      <a-descriptions-item label="成交均价">{{ text(order.averagePrice) }}</a-descriptions-item>
      <a-descriptions-item label="时间" :span="2">{{ fmtTime(order.at) }}</a-descriptions-item>
    </a-descriptions>

    <div v-if="order.note" class="order-note">{{ order.note }}</div>
  </a-card>
</template>

<style scoped>
.card-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.order-symbol {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.order-metrics :deep(.arco-descriptions-item-label) {
  color: var(--color-text-3);
}

.order-metrics :deep(.arco-descriptions-item-value) {
  font-variant-numeric: tabular-nums;
}

.order-note {
  margin-top: 8px;
  padding-top: 8px;
  border-top: 1px solid var(--color-border-1);
  font-size: 12px;
  line-height: 1.6;
  color: var(--color-text-2);
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
