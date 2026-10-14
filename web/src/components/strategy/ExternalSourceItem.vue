<script setup lang="ts">
import { IconDelete, IconPlus } from '@arco-design/web-vue/es/icon'
import type { ExternalDataSource, ExternalHeader } from '../../types'

const props = defineProps<{ source: ExternalDataSource; index: number }>()

const emit = defineEmits<{ (e: 'remove'): void }>()

function addHeader() {
  props.source.headers.push({ key: '', value: '' })
}

function removeHeader(headerIndex: number) {
  props.source.headers.splice(headerIndex, 1)
}

function headerRows(): ExternalHeader[] {
  return props.source.headers
}
</script>

<template>
  <div class="source-item">
    <div class="item-head">
      <span class="item-index">#{{ index + 1 }}</span>
      <button type="button" class="item-remove" @click="emit('remove')">删除</button>
    </div>

    <div class="field-grid">
      <div class="grid-field">
        <span class="grid-label">名称</span>
        <a-input v-model="source.name" size="small" placeholder="如：宏观日历" />
      </div>
      <div class="grid-field">
        <span class="grid-label">方法</span>
        <a-select v-model="source.method" size="small">
          <a-option value="GET">GET</a-option>
          <a-option value="POST">POST</a-option>
        </a-select>
      </div>
      <div class="grid-field">
        <span class="grid-label">刷新间隔（秒）</span>
        <a-input-number v-model="source.refreshSecs" size="small" :min="5" :max="600" />
      </div>
    </div>

    <div class="grid-field">
      <span class="grid-label">接口地址</span>
      <a-input v-model="source.url" size="small" placeholder="https://api.example.com/v1/feed" />
    </div>

    <div class="grid-field">
      <span class="grid-label">取值路径</span>
      <a-input v-model="source.dataPath" size="small" placeholder="如 data.price 或 data.items.0.name" />
      <span class="grid-hint">点分路径，从响应 JSON 中提取目标值；取空时该源本轮缺席</span>
    </div>

    <div class="headers">
      <div v-for="(header, headerIndex) in headerRows()" :key="headerIndex" class="header-row">
        <a-input v-model="header.key" size="mini" placeholder="请求头名称" />
        <a-input v-model="header.value" size="mini" placeholder="值" />
        <a-button size="mini" type="text" @click="removeHeader(headerIndex)">
          <template #icon><IconDelete /></template>
        </a-button>
      </div>
      <div class="headers-tools">
        <a-button size="mini" @click="addHeader">
          <template #icon><IconPlus /></template>
          添加请求头
        </a-button>
        <span class="headers-hint">请求头可能含密钥：导出策略时会自动剥离</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.source-item {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 12px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
}

.item-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.item-index {
  font-size: 12px;
  font-weight: 600;
  color: var(--color-text-2);
}

.item-remove {
  border: none;
  background: transparent;
  padding: 0;
  font-size: 12px;
  color: rgb(var(--danger-6));
  cursor: pointer;
}

.item-remove:hover {
  text-decoration: underline;
}

.field-grid {
  display: grid;
  grid-template-columns: 2fr 1fr 1fr;
  gap: 12px;
}

@media (max-width: 900px) {
  .field-grid {
    grid-template-columns: 1fr;
  }
}

.grid-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.grid-label {
  font-size: 12px;
  color: var(--color-text-3);
}

.grid-hint {
  font-size: 11px;
  color: var(--color-text-3);
}

.headers {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-top: 10px;
  border-top: 1px solid var(--color-border-2);
}

.header-row {
  display: grid;
  grid-template-columns: 1fr 2fr auto;
  gap: 8px;
  align-items: center;
}

.headers-tools {
  display: flex;
  align-items: center;
  gap: 10px;
}

.headers-hint {
  font-size: 11px;
  color: var(--color-text-3);
}
</style>
