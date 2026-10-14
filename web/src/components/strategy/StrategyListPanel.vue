<script setup lang="ts">
import { IconCopy, IconDelete, IconDownload, IconPlus, IconUpload } from '@arco-design/web-vue/es/icon'
import type { Strategy } from '../../types'

defineProps<{ strategies: Strategy[]; selectedId: number | null }>()

const emit = defineEmits<{
  (e: 'select', strategy: Strategy): void
  (e: 'create'): void
  (e: 'import'): void
  (e: 'export', strategy: Strategy): void
  (e: 'duplicate', strategy: Strategy): void
  (e: 'remove', strategy: Strategy): void
}>()
</script>

<template>
  <div class="list-head">
    <span class="list-title">策略</span>
    <span class="list-tools">
      <a-tooltip content="导入策略（.json）">
        <button type="button" class="tool-btn" aria-label="导入策略" @click="emit('import')">
          <IconUpload />
        </button>
      </a-tooltip>
      <a-tooltip content="新建策略">
        <button type="button" class="tool-btn primary" aria-label="新建策略" @click="emit('create')">
          <IconPlus />
        </button>
      </a-tooltip>
    </span>
  </div>

  <div class="list-rows">
    <div
      v-for="strategy in strategies"
      :key="strategy.id"
      class="strategy-card"
      :class="{ selected: strategy.id === selectedId }"
      @click="emit('select', strategy)"
    >
      <div class="card-name" :title="strategy.name">{{ strategy.name }}</div>
      <div v-if="strategy.isActive" class="card-badges">
        <a-tag color="green" size="small">激活中</a-tag>
      </div>
      <div class="card-actions" @click.stop>
        <a-tooltip content="导出">
          <button type="button" class="card-btn" aria-label="导出" @click="emit('export', strategy)">
            <IconDownload />
          </button>
        </a-tooltip>
        <a-tooltip content="复制">
          <button type="button" class="card-btn" aria-label="复制" @click="emit('duplicate', strategy)">
            <IconCopy />
          </button>
        </a-tooltip>
        <a-tooltip content="删除">
          <button type="button" class="card-btn danger" aria-label="删除" @click="emit('remove', strategy)">
            <IconDelete />
          </button>
        </a-tooltip>
      </div>
    </div>
    <div v-if="strategies.length === 0" class="list-empty">暂无策略</div>
  </div>
</template>

<style scoped>
.list-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 8px 6px;
  flex-shrink: 0;
}

.list-title {
  font-size: 12px;
  font-weight: 500;
  color: var(--color-text-3);
}

.list-tools {
  display: flex;
  gap: 4px;
}

.tool-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: var(--color-text-3);
  cursor: pointer;
  transition: color 0.15s, background-color 0.15s;
}

.tool-btn:hover {
  background: var(--color-fill-2);
  color: var(--color-text-1);
}

.tool-btn.primary {
  color: rgb(var(--primary-6));
}

.list-rows {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 0 8px 8px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.strategy-card {
  position: relative;
  padding: 8px 10px;
  border: 1px solid transparent;
  border-radius: 8px;
  cursor: pointer;
  transition: background-color 0.15s, border-color 0.15s;
}

.strategy-card:hover {
  background: var(--color-fill-1);
  border-color: var(--color-border-2);
}

.strategy-card.selected {
  background: rgba(var(--primary-6), 0.08);
  border-color: rgba(var(--primary-6), 0.5);
}

.card-name {
  padding-right: 62px; /* 预留右上角悬浮操作组宽度，名称超长省略 */
  font-size: 14px;
  color: var(--color-text-1);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-badges {
  display: flex;
  gap: 4px;
  margin-top: 4px;
}

.card-actions {
  position: absolute;
  top: 6px;
  right: 6px;
  display: flex;
  gap: 2px;
  opacity: 0;
  transition: opacity 0.15s;
}

.strategy-card:hover .card-actions {
  opacity: 1;
}

.card-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border: none;
  border-radius: 4px;
  background: var(--color-bg-2);
  color: var(--color-text-3);
  cursor: pointer;
  transition: color 0.15s;
}

.card-btn:hover {
  color: rgb(var(--primary-6));
}

.card-btn.danger:hover {
  color: rgb(var(--danger-6));
}

.list-empty {
  padding: 24px 0;
  text-align: center;
  font-size: 12px;
  color: var(--color-text-3);
}
</style>
