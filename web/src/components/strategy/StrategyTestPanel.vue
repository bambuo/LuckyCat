<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconClockCircle, IconPlayArrow } from '@arco-design/web-vue/es/icon'
import { listProviders, runStrategyTest } from '../../api'
import { VARIANTS } from '../../strategy'
import type { Provider, StrategyConfig, StrategyTestResult } from '../../types'
import CodeBlock from './CodeBlock.vue'

const props = defineProps<{ config: StrategyConfig; strategyId: number | null }>()

const providers = ref<Provider[]>([])
const providerId = ref<number | null>(null)
const running = ref(false)
const result = ref<StrategyTestResult | null>(null)

onMounted(async () => {
  try {
    providers.value = await listProviders()
    if (providers.value.length > 0) providerId.value = providers.value[0].id
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '加载模型列表失败')
  }
})

async function run() {
  if (!providerId.value || running.value) return
  running.value = true
  result.value = null
  try {
    result.value = await runStrategyTest(props.config, providerId.value, props.strategyId)
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '测试请求失败')
  } finally {
    running.value = false
  }
}
</script>

<template>
  <div class="test-panel">
    <div class="toolbar">
      <a-select
        :model-value="providerId ?? undefined"
        size="small"
        placeholder="选择模型提供商"
        :disabled="providers.length === 0"
        class="provider-select"
        :style="{ flex: '1 1 0%', minWidth: '0' }"
        @update:model-value="(value) => (providerId = Number(value))"
      >
        <a-option v-for="provider in providers" :key="provider.id" :value="provider.id">
          {{ provider.name }}（{{ provider.model }}）
        </a-option>
      </a-select>
      <a-select v-model="config.variant" size="small" :style="{ width: '92px', flexShrink: 0 }">
        <a-option v-for="variant in VARIANTS" :key="variant.key" :value="variant.key">
          {{ variant.label }}
        </a-option>
      </a-select>
      <a-button
        size="small"
        type="primary"
        status="success"
        :loading="running"
        :disabled="providers.length === 0"
        @click="run"
      >
        <template #icon><IconPlayArrow /></template>
        运行
      </a-button>
    </div>

    <a-alert v-if="providers.length === 0" type="warning">
      请先在「模型提供商」页配置至少一个模型提供商
    </a-alert>

    <div v-if="running" class="panel-empty">
      <a-spin />
      <div class="empty-text">测试运行中，等待模型响应…</div>
    </div>
    <div v-else-if="!result" class="panel-empty">
      <div class="empty-text">点击运行 AI 测试</div>
    </div>

    <div v-else class="panel-result">
      <div class="meta-row">
        <span class="meta-item">
          <IconClockCircle class="meta-icon" />耗时: {{ (result.elapsedMs / 1000).toFixed(2) }}s
          <span class="meta-tokens">· 提示词 ≈ {{ result.estimatedTokens }} tokens</span>
        </span>
        <a-tag size="small" :color="result.dataSource === 'live' ? 'green' : 'orange'">
          {{ result.dataSource === 'live' ? '实时行情' : '数据缺失' }}
        </a-tag>
      </div>

      <a-alert v-if="result.contextWarning" type="warning">{{ result.contextWarning }}</a-alert>
      <a-alert v-if="result.error" type="error">{{ result.error }}</a-alert>

      <div v-if="result.userPrompt" class="result-block">
        <div class="result-title">用户提示词</div>
        <CodeBlock :content="result.userPrompt" :max-height="200" wrap />
      </div>

      <div v-if="result.reasoning" class="result-block">
        <div class="result-title">思维链</div>
        <CodeBlock :content="result.reasoning" variant="success" :max-height="240" wrap />
      </div>

      <div v-if="result.decision" class="result-block">
        <div class="result-title">决策</div>
        <CodeBlock :content="JSON.stringify(result.decision, null, 2)" :max-height="260" />
      </div>

      <div v-if="result.rawOutput" class="result-block">
        <div class="result-title">AI 原始输出</div>
        <CodeBlock :content="result.rawOutput" :max-height="300" wrap />
      </div>
    </div>

    <div class="panel-foot">使用真实 AI 模型测试，不执行交易</div>
  </div>
</template>

<style scoped>
.test-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

/* 注意：a-select 的根元素（span.arco-select-view）不接收父级 scoped 属性（Arco 手动分发 $attrs，
   data-v 不落在其上），因此「弹性收缩」布局必须用模板上的行内 :style（见 .provider-select 与风格选择框），
   写成 scoped 类会静默失效——长标签会把「运行」按钮顶出可视区 */
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
  gap: 10px;
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

.meta-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.meta-item {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 12px;
  color: var(--color-text-2);
}

.meta-icon {
  color: var(--color-text-3);
}

.meta-tokens {
  margin-left: 6px;
  color: var(--color-text-3);
}

.result-title {
  margin-bottom: 6px;
  font-size: 12px;
  font-weight: 500;
  color: var(--color-text-2);
}

.panel-foot {
  margin-top: 4px;
  padding-top: 10px;
  border-top: 1px solid var(--color-border-2);
  font-size: 11px;
  color: var(--color-text-3);
  text-align: center;
}
</style>
