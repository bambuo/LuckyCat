<script setup lang="ts">
import {
  IconCheck,
  IconLoop,
  IconRobot,
  IconSafe,
  IconStorage,
  IconThunderbolt,
} from '@arco-design/web-vue/es/icon'
import type { StrategyConfig } from '../../types'
import CoinSourceEditor from './CoinSourceEditor.vue'
import IndicatorEditor from './IndicatorEditor.vue'
import RiskControlEditor from './RiskControlEditor.vue'

defineProps<{
  config: StrategyConfig
  defaultConfig: StrategyConfig
  name: string
  description: string
  isActive: boolean
  dirty: boolean
  saving: boolean
}>()

const emit = defineEmits<{
  (e: 'update:name', value: string): void
  (e: 'update:description', value: string): void
  (e: 'save'): void
  (e: 'activate'): void
}>()
</script>

<template>
  <div class="editor">
    <!-- 标题区 -->
    <div class="editor-title">
      <div class="title-inputs">
        <input
          class="name-input"
          :value="name"
          placeholder="策略名称"
          maxlength="100"
          @input="emit('update:name', ($event.target as HTMLInputElement).value)"
        />
        <input
          class="desc-input"
          :value="description"
          placeholder="添加策略简介..."
          maxlength="500"
          @input="emit('update:description', ($event.target as HTMLInputElement).value)"
        />
        <span v-if="dirty" class="dirty-mark">● 未保存</span>
      </div>
      <div class="title-actions">
        <a-button v-if="!isActive" status="success" @click="emit('activate')">
          <template #icon><IconCheck /></template>
          激活
        </a-button>
        <a-button type="primary" :disabled="!dirty" :loading="saving" @click="emit('save')">
          {{ saving ? '保存中...' : '保存' }}
        </a-button>
      </div>
    </div>

    <!-- 策略类型：LC-STD-03 §2.2 已下线网格类型，只保留 AI 智能交易 -->
    <div class="type-block">
      <div class="type-block-title">
        <IconThunderbolt class="block-icon" />
        <span>策略类型</span>
      </div>
      <div class="type-cards single">
        <div class="type-card ai_trading active static">
          <IconRobot class="type-icon" />
          <div class="type-body">
            <div class="type-name">AI 智能交易</div>
            <div class="type-desc">AI 分析市场并自主决策买卖；网格类型已下线</div>
          </div>
          <a-tag size="small" color="green">当前</a-tag>
        </div>
      </div>
    </div>

    <!-- 折叠分区；展开图标置右，避免与分区图标挤在一起 -->
    <a-collapse
      :default-active-key="['coin']"
      :bordered="false"
      expand-icon-position="right"
      class="sections"
    >
      <a-collapse-item key="coin" class="section-item">
        <template #header>
          <span class="section-title"><IconStorage class="section-icon primary" />币种来源</span>
        </template>
        <CoinSourceEditor :config="config.coinSource" />
      </a-collapse-item>

      <a-collapse-item key="indicators" class="section-item">
        <template #header>
          <span class="section-title"><IconThunderbolt class="section-icon success" />技术指标</span>
        </template>
        <IndicatorEditor :config="config.indicators" :indicator-defaults="defaultConfig.indicators" />
      </a-collapse-item>

      <a-collapse-item key="risk" class="section-item">
        <template #header>
          <span class="section-title"><IconSafe class="section-icon danger" />风控参数</span>
        </template>
        <RiskControlEditor :config="config.riskControl" />
      </a-collapse-item>

      <a-collapse-item key="context" class="section-item">
        <template #header>
          <span class="section-title"><IconLoop class="section-icon success" />上下文增强</span>
        </template>
        <div class="context-body">
          <div class="context-row">
            <div class="context-text">
              <div class="context-name">历史决策记忆</div>
              <div class="section-desc">决策时注入最近 8 条决策记录（AI 测试成功出决策即落库）；关闭仅停止注入，记录仍持续保存</div>
            </div>
            <a-switch v-model="config.decisionMemoryEnabled" />
          </div>
          <div class="context-row">
            <div class="context-text">
              <div class="context-name">实时消息面</div>
              <div class="section-desc">决策时注入最新消息面数据；作为外部数据源总开关，关闭后 AI 测试不拉取、不注入</div>
            </div>
            <a-switch v-model="config.newsEnabled" />
          </div>
        </div>
      </a-collapse-item>
    </a-collapse>
  </div>
</template>

<style scoped>
.editor {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.editor-title {
  display: flex;
  align-items: flex-start;
  gap: 12px;
}

.title-inputs {
  flex: 1;
  min-width: 0;
}

/* 无边框输入（设计稿 §3.1） */
.name-input {
  display: block;
  width: 100%;
  max-width: 240px;
  padding: 0;
  border: none;
  outline: none;
  background: transparent;
  font-size: 18px;
  font-weight: 700;
  color: var(--color-text-1);
}

.name-input::placeholder {
  color: var(--color-text-3);
}

.desc-input {
  display: block;
  width: 100%;
  max-width: 420px;
  margin-top: 6px;
  padding: 0;
  border: none;
  outline: none;
  background: transparent;
  font-size: 12px;
  color: var(--color-text-2);
}

.desc-input::placeholder {
  color: var(--color-text-3);
}

.dirty-mark {
  display: inline-block;
  margin-top: 6px;
  font-size: 12px;
  color: rgb(var(--primary-6));
}

.title-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.type-block {
  padding: 16px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-2);
}

.type-block-title {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 12px;
  font-size: 14px;
  color: var(--color-text-1);
}

.block-icon {
  color: rgb(var(--primary-6));
  font-size: 16px;
}

.type-cards {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}

.type-cards.single {
  grid-template-columns: 1fr;
}

.type-card {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 14px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-2);
}

.type-card.static {
  cursor: default;
}

.type-card.ai_trading.active {
  border-color: rgb(var(--primary-6));
  background: rgba(var(--primary-6), 0.08);
}

.type-card .type-body {
  flex: 1;
  min-width: 0;
}

.type-icon {
  font-size: 16px;
  margin-top: 2px;
}

.type-card.ai_trading.active .type-icon {
  color: rgb(var(--primary-6));
}

.type-name {
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}

.type-desc {
  margin-top: 2px;
  font-size: 12px;
  color: var(--color-text-3);
}

.sections :deep(.section-item > .arco-collapse-item-header) {
  padding-left: 0;
  padding-right: 0;
  background: transparent;
}

.sections :deep(.section-item > .arco-collapse-item-content) {
  padding-left: 0;
  padding-right: 0;
  background: transparent;
}

.section-title {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}

.section-icon {
  font-size: 18px;
}

.section-icon.primary { color: rgb(var(--primary-6)); }
.section-icon.success { color: rgb(var(--success-6)); }
.section-icon.danger { color: rgb(var(--danger-6)); }

.section-desc {
  font-size: 12px;
  color: var(--color-text-3);
}

.context-body {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.context-row {
  display: flex;
  align-items: center;
  gap: 16px;
}

.context-text {
  flex: 1;
  min-width: 0;
}

.context-name {
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}
</style>
