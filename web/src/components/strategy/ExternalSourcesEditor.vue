<script setup lang="ts">
import type { ExternalDataSource } from '../../types'
import ExternalSourceItem from './ExternalSourceItem.vue'

const props = defineProps<{ sources: ExternalDataSource[] }>()

function removeSource(index: number) {
  props.sources.splice(index, 1)
}
</script>

<template>
  <div class="sources-editor">
    <div v-if="sources.length === 0" class="sources-empty">
      暂无外部数据源（可用上方一键预设添加消息面源）
    </div>

    <ExternalSourceItem
      v-for="(item, index) in sources"
      :key="index"
      :source="item"
      :index="index"
      @remove="removeSource(index)"
    />
  </div>
</template>

<style scoped>
.sources-editor {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.sources-empty {
  padding: 16px;
  border: 1px dashed var(--color-border-2);
  border-radius: 8px;
  text-align: center;
  font-size: 13px;
  color: var(--color-text-3);
}
</style>
