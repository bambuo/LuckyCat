<script setup lang="ts">
import { ref } from 'vue'
import { Message } from '@arco-design/web-vue'
import {
  IconClockCircle,
  IconDashboard,
  IconPlus,
  IconStar,
  IconThunderbolt,
} from '@arco-design/web-vue/es/icon'
import {
  createEmptyExternalSource,
  EXTERNAL_SOURCE_PRESETS,
  INDICATOR_CATALOG,
  SENTIMENT_CATALOG,
  TIMEFRAME_GROUPS,
} from '../../strategy'
import type { ExternalSourcePreset } from '../../strategy'
import type { IndicatorConfig, IndicatorSettings, SentimentConfig } from '../../types'
import ExternalSourcesEditor from './ExternalSourcesEditor.vue'

type IndicatorKey = 'ema' | 'macd' | 'rsi' | 'atr' | 'boll' | 'vwap'

const props = defineProps<{
  config: IndicatorSettings
  indicatorDefaults?: Partial<Record<IndicatorKey, IndicatorConfig>>
}>()

// 周期药丸：点击切换 selectedTimeframes；主周期 = klines.primaryTimeframe（★ 与色环）
function toggleTimeframe(timeframe: string) {
  const selected = props.config.klines.selectedTimeframes
  const index = selected.indexOf(timeframe)
  if (index >= 0) {
    if (selected.length === 1) return // 至少保留一个周期
    if (props.config.klines.primaryTimeframe === timeframe) {
      props.config.klines.primaryTimeframe = selected.filter((t) => t !== timeframe)[0]
    }
    selected.splice(index, 1)
  } else {
    selected.push(timeframe)
  }
}

// 点击 ★ 设为主周期（未选中时会先加入已选周期）
function setPrimary(timeframe: string) {
  if (!props.config.klines.selectedTimeframes.includes(timeframe)) {
    props.config.klines.selectedTimeframes.push(timeframe)
  }
  props.config.klines.primaryTimeframe = timeframe
}

// 指标周期输入：本地草稿文本 + 失焦/回车提交（避免输入中途被解析回写打断，如「20,」被吞掉逗号），非法项忽略
const periodDrafts = ref<Record<string, string>>({})

function periodText(key: IndicatorKey, indicator: IndicatorConfig): string {
  return periodDrafts.value[key] ?? indicator.periods.join(',')
}

function periodPlaceholder(key: IndicatorKey, indicator: IndicatorConfig): string {
  const periods = props.indicatorDefaults?.[key]?.periods ?? indicator.periods
  return periods.length > 0 ? `默认 ${periods.join(',')}` : '如 20,50'
}

function setPeriodDraft(key: IndicatorKey, value: string) {
  periodDrafts.value[key] = value
}

function commitPeriods(key: IndicatorKey, indicator: IndicatorConfig) {
  const draft = periodDrafts.value[key]
  if (draft === undefined) return
  indicator.periods = draft
    .split(',')
    .map((part) => Number.parseInt(part.trim(), 10))
    .filter((n) => Number.isFinite(n) && n > 0)
  periodDrafts.value[key] = indicator.periods.join(',')
}

function indicatorOf(key: IndicatorKey): IndicatorConfig {
  return props.config[key]
}

function sentimentOf(key: 'volume' | 'openInterest' | 'fundingRate'): SentimentConfig {
  return props.config[key]
}

function timeframePillStyle(group: { color: string }, timeframe: string): Record<string, string> | undefined {
  if (!props.config.klines.selectedTimeframes.includes(timeframe)) return undefined
  return {
    borderColor: group.color,
    color: group.color,
    background: `color-mix(in srgb, ${group.color} 15%, transparent)`,
  }
}

function indicatorCardStyle(color: string, enabled: boolean): Record<string, string> | undefined {
  if (!enabled) return undefined
  return {
    borderColor: `color-mix(in srgb, ${color} 35%, transparent)`,
    background: `color-mix(in srgb, ${color} 8%, transparent)`,
  }
}

// 外部数据源：一键预设（同 URL 提示去重）与空源添加
function addExternalPreset(preset: ExternalSourcePreset) {
  const source = preset.make()
  if (props.config.externalDataSources.some((s) => s.url === source.url)) {
    Message.info(`已存在相同地址的数据源（${preset.label}）`)
    return
  }
  props.config.externalDataSources.push(source)
}

function addExternalSource() {
  props.config.externalDataSources.push(createEmptyExternalSource())
}
</script>

<template>
  <div class="indicator-editor">
    <!-- 1. 市场数据 -->
    <div class="block">
      <div class="block-head">
        <IconDashboard class="block-icon primary" />
        <span class="block-title">市场数据</span>
        <span class="kline-count">
          K 线数量
          <a-input-number
            v-model="config.klines.primaryCount"
            :min="10"
            :max="200"
            :style="{ width: '64px' }"
            size="small"
          />
        </span>
      </div>

      <!-- 必选行：原始 K 线（金框 + 必须徽章 + 勾选框选中且禁用） -->
      <div class="required-row">
        <span class="required-icon"><IconThunderbolt /></span>
        <div class="required-body">
          <div class="required-name">
            原始 K 线数据
            <a-tag color="gold" size="small">必须</a-tag>
          </div>
          <div class="required-desc">所有判断的基础行情数据，始终启用以供 AI 计算各指标</div>
        </div>
        <a-checkbox :model-value="true" disabled />
      </div>

      <!-- 时间周期：四分类药丸 -->
      <div class="timeframe-rows">
        <div v-for="group in TIMEFRAME_GROUPS" :key="group.key" class="timeframe-row">
          <span class="timeframe-label" :style="{ color: group.color }">{{ group.label }}</span>
          <span class="timeframe-pills">
            <button
              v-for="timeframe in group.options"
              :key="timeframe"
              type="button"
              class="pill"
              :class="{ active: config.klines.selectedTimeframes.includes(timeframe) }"
              :style="timeframePillStyle(group, timeframe)"
              @click="toggleTimeframe(timeframe)"
            >
              {{ timeframe }}
              <span
                v-if="config.klines.primaryTimeframe === timeframe"
                class="pill-star"
                :style="{ color: group.color }"
                @click.stop="setPrimary(timeframe)"
              >★</span>
            </button>
          </span>
        </div>
      </div>
      <!-- 多周期参考开关：开启后行情快照按全部已选周期逐一提供 -->
      <div class="multi-row">
        <div class="multi-text">
          <div class="multi-name">多周期参考</div>
          <div class="multi-desc">
            开启后，行情快照按全部已选周期逐一提供（主周期按上方 K 线数量，其余周期各 12 根），用于多周期确认
          </div>
        </div>
        <a-switch v-model="config.klines.enableMultiTimeframe" />
      </div>
      <div class="block-desc">点击药丸切换周期；点击 ★ 设为主周期（主周期带色环）</div>
    </div>

    <!-- 2. 技术指标 -->
    <div class="block">
      <div class="block-head">
        <IconThunderbolt class="block-icon success" />
        <span class="block-title">技术指标</span>
      </div>
      <div class="hint-box">AI 可基于原始 K 线自行计算指标数值；此处勾选用于声明策略关注哪些指标及周期</div>

      <div class="card-grid">
        <div
          v-for="indicator in INDICATOR_CATALOG"
          :key="indicator.key"
          class="indicator-card"
          :style="indicatorCardStyle(indicator.color, indicatorOf(indicator.key).enabled)"
          @click="indicatorOf(indicator.key).enabled = !indicatorOf(indicator.key).enabled"
        >
          <span class="card-dot" :style="{ background: indicator.color }" />
          <span class="card-name">{{ indicator.label }}</span>
          <a-checkbox
            :model-value="indicatorOf(indicator.key).enabled"
            @click.stop
            @update:model-value="(value) => (indicatorOf(indicator.key).enabled = value === true)"
          />
          <div class="card-desc">{{ indicator.desc }}</div>
          <div v-if="indicatorOf(indicator.key).enabled" class="period-input" @click.stop>
            <a-input
              :model-value="periodText(indicator.key, indicatorOf(indicator.key))"
              size="mini"
              :placeholder="periodPlaceholder(indicator.key, indicatorOf(indicator.key))"
              @input="(value) => setPeriodDraft(indicator.key, value)"
              @blur="commitPeriods(indicator.key, indicatorOf(indicator.key))"
              @press-enter="commitPeriods(indicator.key, indicatorOf(indicator.key))"
            />
          </div>
        </div>
      </div>
    </div>

    <!-- 3. 市场情绪 -->
    <div class="block">
      <div class="block-head">
        <IconStar class="block-icon gold" />
        <span class="block-title">市场情绪</span>
      </div>
      <div class="block-desc">
        持仓量与资金费率在 AI 测试时逐标的拉取并随行情快照注入（各自开关控制拉取，关闭即不拉不注入）；成交量数据随 K 线表提供
      </div>
      <div class="card-grid three">
        <div
          v-for="sentiment in SENTIMENT_CATALOG"
          :key="sentiment.key"
          class="indicator-card"
          :style="indicatorCardStyle(sentiment.color, sentimentOf(sentiment.key).enabled)"
          @click="sentimentOf(sentiment.key).enabled = !sentimentOf(sentiment.key).enabled"
        >
          <span class="card-dot" :style="{ background: sentiment.color }" />
          <span class="card-name">{{ sentiment.label }}</span>
          <a-checkbox
            :model-value="sentimentOf(sentiment.key).enabled"
            @click.stop
            @update:model-value="(value) => (sentimentOf(sentiment.key).enabled = value === true)"
          />
          <div class="card-desc">{{ sentiment.desc }}</div>
        </div>
      </div>
    </div>

    <!-- 4. 外部数据源 -->
    <div class="block">
      <div class="block-head">
        <IconClockCircle class="block-icon primary" />
        <span class="block-title">外部数据源</span>
        <span class="head-actions">
          <a-button
            v-for="preset in EXTERNAL_SOURCE_PRESETS"
            :key="preset.key"
            size="mini"
            @click="addExternalPreset(preset)"
          >
            <template #icon><IconPlus /></template>
            {{ preset.label }}
          </a-button>
          <a-button size="mini" type="primary" @click="addExternalSource">
            <template #icon><IconPlus /></template>
            添加数据源
          </a-button>
        </span>
      </div>
      <div class="block-desc">
        AI 测试时并行拉取并注入用户提示词；每源最多 10 条、按刷新间隔缓存，失败源本轮缺席；Prompt 预览仅声明数据源；受「上下文增强 · 实时消息面」总开关控制
      </div>
      <ExternalSourcesEditor :sources="config.externalDataSources" />
    </div>
  </div>
</template>

<style scoped>
.indicator-editor {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.block {
  display: flex;
  flex-direction: column;
  gap: 10px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  overflow: hidden;
}

.block-head {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 12px;
  background: var(--color-bg-2);
  border-bottom: 1px solid var(--color-border-2);
}

.block-icon {
  font-size: 16px;
}

.block-icon.primary { color: rgb(var(--primary-6)); }
.block-icon.success { color: rgb(var(--success-6)); }
.block-icon.gold { color: #f0b90b; }

.block-title {
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}

.kline-count {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
  font-size: 12px;
  color: var(--color-text-3);
}

.head-actions {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
  flex-wrap: wrap;
}

.block > :not(.block-head) {
  margin-left: 12px;
  margin-right: 12px;
}

.block > :last-child {
  margin-bottom: 12px;
}

.block-desc {
  font-size: 12px;
  color: var(--color-text-3);
}

/* —— 多周期参考开关行 —— */
.multi-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 12px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
}

.multi-text {
  flex: 1;
  min-width: 0;
}

.multi-name {
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}

.multi-desc {
  margin-top: 2px;
  font-size: 12px;
  color: var(--color-text-3);
}

/* —— 必选行 —— */
.required-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px;
  border: 1px solid rgba(240, 185, 11, 0.2);
  border-radius: 8px;
  background: rgba(240, 185, 11, 0.08);
}

.required-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
  background: rgba(240, 185, 11, 0.15);
  color: rgb(var(--primary-6));
  font-size: 16px;
  flex-shrink: 0;
}

.required-body {
  flex: 1;
  min-width: 0;
}

.required-name {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}

.required-desc {
  margin-top: 2px;
  font-size: 12px;
  color: var(--color-text-3);
}

/* —— 时间周期 —— */
.timeframe-rows {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.timeframe-row {
  display: flex;
  align-items: center;
  gap: 10px;
}

.timeframe-label {
  width: 40px;
  flex-shrink: 0;
  font-size: 10px;
}

.timeframe-pills {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.pill {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 4px 8px;
  border: 1px solid var(--color-border-2);
  border-radius: 4px;
  background: transparent;
  font-size: 12px;
  font-weight: 500;
  color: var(--color-text-2);
  opacity: 0.4;
  cursor: pointer;
  transition: opacity 0.15s, border-color 0.15s, background-color 0.15s;
}

.pill:hover {
  opacity: 0.7;
}

.pill.active {
  opacity: 1;
}

.pill-star {
  font-size: 10px;
  line-height: 1;
}

/* —— 提示盒 —— */
.hint-box {
  padding: 8px;
  border-radius: 4px;
  background: rgba(var(--success-6), 0.05);
  font-size: 10px;
  color: var(--color-text-3);
}

/* —— 指标 / 情绪卡 —— */
.card-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
}

.card-grid.three {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.indicator-card {
  display: grid;
  grid-template-columns: auto 1fr auto;
  grid-template-areas:
    "dot name check"
    "desc desc desc"
    "period period period";
  align-items: center;
  gap: 4px 6px;
  padding: 10px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
  cursor: pointer;
  transition: border-color 0.15s, background-color 0.15s;
}

.card-dot {
  grid-area: dot;
  width: 8px;
  height: 8px;
  border-radius: 50%;
}

.card-name {
  grid-area: name;
  font-size: 12px;
  font-weight: 500;
  color: var(--color-text-1);
}

.indicator-card :deep(.arco-checkbox) {
  grid-area: check;
  justify-self: end;
  padding: 0;
}

.card-desc {
  grid-area: desc;
  font-size: 10px;
  color: var(--color-text-3);
}

.period-input {
  grid-area: period;
  margin-top: 4px;
}
</style>
