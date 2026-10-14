<script setup lang="ts">
import { ref } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconRefresh } from '@arco-design/web-vue/es/icon'
import { previewStrategyPrompt } from '../../api'
import { VARIANTS } from '../../strategy'
import type { PromptPreview, StrategyConfig } from '../../types'
import CodeBlock from './CodeBlock.vue'

const props = defineProps<{ config: StrategyConfig }>()

const preview = ref<PromptPreview | null>(null)
const loading = ref(false)

async function generate() {
  if (loading.value) return
  loading.value = true
  try {
    preview.value = await previewStrategyPrompt(props.config)
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '生成失败')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="preview-panel">
    <div class="toolbar">
      <a-select v-model="config.variant" size="small" :style="{ width: '120px' }">
        <a-option v-for="variant in VARIANTS" :key="variant.key" :value="variant.key">
          {{ variant.label }}
        </a-option>
      </a-select>
      <a-button type="primary" size="small" :loading="loading" @click="generate">
        <template #icon><IconRefresh /></template>
        {{ preview ? '刷新' : '生成 Prompt' }}
      </a-button>
    </div>

    <div v-if="!preview" class="panel-empty">
      <div class="empty-text">点击生成 Prompt 预览</div>
    </div>

    <div v-else class="panel-result">
      <div class="summary-card">
        <div v-for="item in preview.summary" :key="item.label" class="summary-item">
          <div class="summary-label">{{ item.label }}</div>
          <div class="summary-value">{{ item.value }}</div>
        </div>
      </div>

      <div class="prompt-block">
        <div class="prompt-head">
          <span class="prompt-title">System Prompt</span>
          <a-tag size="small">{{ preview.systemPrompt.length }} 字符</a-tag>
        </div>
        <CodeBlock :content="preview.systemPrompt" :max-height="400" variant="gold" wrap />
      </div>
    </div>
  </div>
</template>

<style scoped>
.preview-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
}

.panel-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 48px 0;
  color: var(--color-text-3);
}

.empty-text {
  font-size: 13px;
}

.panel-result {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.summary-card {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px 8px;
  padding: 12px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
}

.summary-label {
  font-size: 11px;
  color: var(--color-text-3);
}

.summary-value {
  margin-top: 2px;
  font-size: 12px;
  color: var(--color-text-1);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.prompt-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 6px;
}

.prompt-title {
  font-size: 12px;
  font-weight: 500;
  color: var(--color-text-2);
}
</style>
