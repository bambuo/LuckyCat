<script setup lang="ts">
import { computed, ref } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconRefresh } from '@arco-design/web-vue/es/icon'
import { previewStrategyPrompts } from '../../api'
import { VARIANTS } from '../../strategy'
import type { PromptPreviewResult, StageRole, StrategyConfig } from '../../types'
import CodeBlock from './CodeBlock.vue'

const props = defineProps<{ config: StrategyConfig }>()

const preview = ref<PromptPreviewResult | null>(null)
const loading = ref(false)
const errorText = ref<string | null>(null)

const ROLE_ORDER: StageRole[] = ['analyst', 'trader', 'risk', 'executor', 'reviewer']

const ROLE_LABELS: Record<StageRole, string> = {
  analyst: '分析师',
  trader: '交易员',
  risk: '风控官',
  executor: '执行员',
  reviewer: '复盘员',
}

// 固定遍历五角色：后端漏返回时仍保留该行并明确提示，不静默隐藏
const roleEntries = computed(() =>
  ROLE_ORDER.map((role) => ({
    role,
    label: ROLE_LABELS[role],
    item: preview.value?.roles.find((entry) => entry.role === role) ?? null,
  })),
)

const totalTokens = computed(
  () => preview.value?.roles.reduce((sum, entry) => sum + entry.estimatedTokens, 0) ?? 0,
)

async function generate() {
  if (loading.value) return
  loading.value = true
  errorText.value = null
  try {
    preview.value = await previewStrategyPrompts(props.config)
  } catch (err) {
    const message = err instanceof Error ? err.message : '生成预览失败'
    errorText.value = message
    Message.error(message)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="preview-panel">
    <div class="toolbar">
      <a-select v-model="config.variant" size="small" :style="{ width: '104px', flexShrink: 0 }">
        <a-option v-for="variant in VARIANTS" :key="variant.key" :value="variant.key">
          {{ variant.label }}
        </a-option>
      </a-select>
      <a-button
        type="primary"
        size="small"
        :loading="loading"
        :style="{ flex: '1 1 0%' }"
        @click="generate"
      >
        <template #icon><IconRefresh /></template>
        {{ preview ? '刷新预览' : '预览 Prompt' }}
      </a-button>
    </div>

    <a-alert v-if="errorText" type="error">{{ errorText }}</a-alert>

    <div v-if="!preview" class="panel-empty">
      <div class="empty-title">五角色系统指令预览</div>
      <div class="empty-text">预览不调用模型、不读写决策记忆；点击上方按钮生成。</div>
    </div>

    <template v-else>
      <div class="preview-summary">
        <span>共 {{ preview.roles.length }} 个角色</span>
        <span class="summary-tokens">估算合计 {{ totalTokens.toLocaleString() }} tokens</span>
      </div>

      <div v-for="entry in roleEntries" :key="entry.role" class="role-block">
        <div class="role-head">
          <span class="role-name">{{ entry.label }}</span>
          <a-tag v-if="entry.item" size="small">
            ≈ {{ entry.item.estimatedTokens.toLocaleString() }} tokens
          </a-tag>
          <a-tag v-else size="small" color="orange">未返回</a-tag>
        </div>
        <CodeBlock
          v-if="entry.item"
          :content="entry.item.prompt"
          :max-height="320"
          variant="gold"
          wrap
        />
        <div v-else class="role-missing">后端未返回该角色的系统指令。</div>
      </div>

      <div class="preview-foot">
        预览为系统指令全文（角色配置一致）；token 为 CJK 感知粗估。响应不包含逐角色记忆命中标记，预览本身不注入记忆。
      </div>
    </template>
  </div>
</template>

<style scoped>
.preview-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
}

.panel-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 40px 16px;
  text-align: center;
  color: var(--color-text-3);
}

.empty-title {
  font-size: 13px;
  color: var(--color-text-2);
}

.empty-text {
  font-size: 11px;
  line-height: 1.6;
}

.preview-summary {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 11px;
  color: var(--color-text-3);
}

.summary-tokens {
  font-variant-numeric: tabular-nums;
}

.role-block {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.role-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.role-name {
  font-size: 12px;
  font-weight: 600;
  color: var(--color-text-1);
}

.role-missing {
  padding: 8px 10px;
  border: 1px dashed var(--color-border-2);
  border-radius: 8px;
  font-size: 11px;
  color: var(--color-text-3);
}

.preview-foot {
  padding-top: 8px;
  border-top: 1px solid var(--color-border-2);
  font-size: 11px;
  line-height: 1.6;
  color: var(--color-text-3);
}
</style>
