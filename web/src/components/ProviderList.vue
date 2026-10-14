<script setup lang="ts">
import {
  IconCode,
  IconDelete,
  IconEdit,
  IconPlayArrow,
  IconPlus,
} from '@arco-design/web-vue/es/icon'
import { letterOf, matchPreset } from '../presets'
import { PROVIDER_TYPE_LABELS } from '../types'
import type { Provider } from '../types'

defineProps<{ providers: Provider[]; loading: boolean }>()

const emit = defineEmits<{
  (e: 'create'): void
  (e: 'verify', provider: Provider): void
  (e: 'edit', provider: Provider): void
  (e: 'remove', provider: Provider): void
}>()

function rowIcon(provider: Provider): { icon: string; letter: string } {
  const preset = matchPreset(provider)
  if (preset?.icon) return { icon: preset.icon, letter: '' }
  if (preset) return { icon: '', letter: preset.letter ?? letterOf(preset.label) }
  return { icon: '', letter: letterOf(provider.name) }
}
</script>

<template>
  <div v-if="loading" class="list-loading">
    <a-spin />
  </div>
  <div v-else-if="providers.length === 0" class="list-empty">
    <div class="empty-icon"><IconCode /></div>
    <div class="empty-text">暂无提供商</div>
    <a-button size="small" @click="emit('create')">
      <template #icon><IconPlus /></template>
      添加第一个提供商
    </a-button>
  </div>
  <div v-else class="provider-rows">
    <div v-for="provider in providers" :key="provider.id" class="provider-row">
      <div class="row-icon">
        <img v-if="rowIcon(provider).icon" :src="rowIcon(provider).icon" :alt="provider.name" />
        <span v-else class="row-letter">{{ rowIcon(provider).letter }}</span>
      </div>
      <div class="row-main">
        <div class="row-name">{{ provider.name }}</div>
        <div class="row-sub">{{ provider.model }}</div>
      </div>
      <div class="row-meta">
        <span class="row-key">{{ provider.apiKeyMasked || '—' }}</span>
        <a-tag color="gray">{{ PROVIDER_TYPE_LABELS[provider.type] }}</a-tag>
      </div>
      <div class="row-actions">
        <a-button size="small" type="outline" status="success" @click="emit('verify', provider)">
          <template #icon><IconPlayArrow /></template>
          验证
        </a-button>
        <a-button size="small" type="outline" status="warning" @click="emit('edit', provider)">
          <template #icon><IconEdit /></template>
          编辑
        </a-button>
        <a-popconfirm content="确定删除该提供商？" @ok="emit('remove', provider)">
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

.provider-rows {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.provider-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
  transition: background-color 0.15s;
}

.provider-row:hover {
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
  width: 20px;
  height: 20px;
}

.row-letter {
  font-size: 13px;
  font-weight: 700;
  color: #722ed1;
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
