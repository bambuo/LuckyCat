<script setup lang="ts">
import {
  IconDelete,
  IconEdit,
  IconPlayArrow,
  IconPlus,
  IconSwap,
} from '@arco-design/web-vue/es/icon'
import { exchangeLabel, exchangeLetter, getExchangePreset } from '../exchanges'
import type { ExchangeAccount } from '../types'

defineProps<{ accounts: ExchangeAccount[]; loading: boolean }>()

const emit = defineEmits<{
  (e: 'create'): void
  (e: 'verify', account: ExchangeAccount): void
  (e: 'edit', account: ExchangeAccount): void
  (e: 'remove', account: ExchangeAccount): void
}>()

function iconOf(account: ExchangeAccount): string {
  return getExchangePreset(account.exchangeType)?.icon ?? ''
}
</script>

<template>
  <div v-if="loading" class="list-loading">
    <a-spin />
  </div>
  <div v-else-if="accounts.length === 0" class="list-empty">
    <div class="empty-icon"><IconSwap /></div>
    <div class="empty-text">暂无已配置的交易所</div>
    <a-button size="small" @click="emit('create')">
      <template #icon><IconPlus /></template>
      添加第一个交易所
    </a-button>
  </div>
  <div v-else class="exchange-rows">
    <div v-for="account in accounts" :key="account.id" class="exchange-row">
      <div class="row-icon">
        <img
          v-if="iconOf(account)"
          :src="iconOf(account)"
          :alt="exchangeLabel(account.exchangeType)"
        />
        <span v-else class="row-letter">{{ exchangeLetter(account.exchangeType) }}</span>
      </div>
      <div class="row-main">
        <div class="row-name">{{ exchangeLabel(account.exchangeType).toUpperCase() }}</div>
        <div class="row-sub">{{ account.accountName }}</div>
      </div>
      <div class="row-meta">
        <span class="row-key">{{ account.apiKeyMasked || '—' }}</span>
        <a-tag v-if="account.passphraseMasked" color="gray">口令</a-tag>
      </div>
      <div class="row-actions">
        <a-button size="small" type="outline" status="success" @click="emit('verify', account)">
          <template #icon><IconPlayArrow /></template>
          验证
        </a-button>
        <a-button size="small" type="outline" status="warning" @click="emit('edit', account)">
          <template #icon><IconEdit /></template>
          编辑
        </a-button>
        <a-popconfirm content="确定删除该交易所账户？" @ok="emit('remove', account)">
          <a-button size="small" type="outline" status="danger">
            <template #icon><IconDelete /></template>
            删除
          </a-button>
        </a-popconfirm>
      </div>
    </div>
  </div>
</template>

<style scoped>
.list-loading {
  display: flex;
  justify-content: center;
  padding: 48px 0;
}

.list-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  padding: 40px 0;
}

.empty-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 48px;
  height: 48px;
  border: 1px solid var(--color-border-2);
  border-radius: 50%;
  color: var(--color-text-3);
  font-size: 20px;
}

.empty-text {
  font-size: 14px;
  color: var(--color-text-3);
}

.exchange-rows {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.exchange-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
  transition: background-color 0.15s;
}

.exchange-row:hover {
  background: var(--color-fill-1);
}

.row-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border-radius: 50%;
  background: var(--color-fill-2);
  flex-shrink: 0;
}

.row-icon img {
  width: 22px;
  height: 22px;
  border-radius: 4px;
}

.row-letter {
  font-size: 13px;
  font-weight: 700;
  color: rgb(var(--primary-6));
}

.row-main {
  flex: 1;
  min-width: 0;
}

.row-name {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-sub {
  font-size: 12px;
  color: var(--color-text-3);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-meta {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-shrink: 0;
}

.row-key {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  color: var(--color-text-3);
}

.row-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}
</style>
