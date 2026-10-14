<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  IconArrowFall,
  IconArrowRise,
  IconList,
  IconLoop,
  IconMinusCircle,
  IconPlus,
  IconStorage,
} from '@arco-design/web-vue/es/icon'
import { COIN_SOURCE_CATALOG, COIN_SOURCE_EXCHANGES, LIMIT_OPTIONS } from '../../strategy'
import type { CoinSourceConfig, CoinSourceType } from '../../types'

const props = defineProps<{ config: CoinSourceConfig }>()

const coinInput = ref('')
const excludeInput = ref('')
const mixedCoinInput = ref('')

// —— 子面板配色（设计稿 §4.2-5：评分榜金 / OI 增绿 / OI 减红 / 混合蓝）——
const PANEL_THEME: Record<string, string> = {
  score: '#f0b90b',
  oi_top: 'rgb(var(--success-6))',
  oi_low: 'rgb(var(--danger-6))',
  mixed: 'rgb(var(--primary-6))',
}

// 标签墙：输入自动大写、去空格；回车或按钮提交；重复忽略
// 不做 USDT 后缀补全（后端拉行情时补 /USDT），不做 xyz: 股票前缀（本项目无该市场）
function addCoin() {
  const value = coinInput.value.trim().toUpperCase()
  if (!value) return
  if (!props.config.staticCoins.includes(value)) props.config.staticCoins.push(value)
  coinInput.value = ''
}

function addMixedCoin() {
  const value = mixedCoinInput.value.trim().toUpperCase()
  if (!value) return
  if (!props.config.staticCoins.includes(value)) props.config.staticCoins.push(value)
  mixedCoinInput.value = ''
}

function addExcluded() {
  const value = excludeInput.value.trim().toUpperCase()
  if (!value) return
  if (!props.config.excludedCoins.includes(value)) props.config.excludedCoins.push(value)
  excludeInput.value = ''
}

function removeCoin(value: string) {
  props.config.staticCoins = props.config.staticCoins.filter((c) => c !== value)
}

function removeExcluded(value: string) {
  props.config.excludedCoins = props.config.excludedCoins.filter((c) => c !== value)
}

// 选择来源类型；单选类来源自动打开对应开关（避免「选中即关闭」的困惑态）
function setSourceType(type: CoinSourceType) {
  props.config.sourceType = type
  if (type === 'score') props.config.useScore = true
  else if (type === 'oi_top') props.config.useOiTop = true
  else if (type === 'oi_low') props.config.useOiLow = true
}

type SingleSourceType = 'score' | 'oi_top' | 'oi_low'

function isSourceEnabled(type: SingleSourceType): boolean {
  return type === 'score' ? props.config.useScore : type === 'oi_top' ? props.config.useOiTop : props.config.useOiLow
}

function setSourceEnabled(type: SingleSourceType, value: boolean) {
  if (type === 'score') props.config.useScore = value
  else if (type === 'oi_top') props.config.useOiTop = value
  else props.config.useOiLow = value
}

function sourceLimit(type: SingleSourceType): number {
  return type === 'score' ? props.config.scoreLimit : type === 'oi_top' ? props.config.oiTopLimit : props.config.oiLowLimit
}

function setSourceLimit(type: SingleSourceType, value: number) {
  if (type === 'score') props.config.scoreLimit = value
  else if (type === 'oi_top') props.config.oiTopLimit = value
  else props.config.oiLowLimit = value
}

// 混合模式：选中来源（checkbox）与各来源 Limit；自定义来源复用 staticCoins
const mixedCards = computed(() => [
  { key: 'score' as const, label: '综合评分榜', checked: props.config.useScore, limit: props.config.scoreLimit, theme: '#f0b90b' },
  { key: 'oi_top' as const, label: 'OI 持仓增加', checked: props.config.useOiTop, limit: props.config.oiTopLimit, theme: 'rgb(var(--success-6))' },
  { key: 'oi_low' as const, label: 'OI 持仓减少', checked: props.config.useOiLow, limit: props.config.oiLowLimit, theme: 'rgb(var(--danger-6))' },
])

// 汇总行（设计稿：底 bg-1 + 边框；文案「最多 N 个币种」+ 组合摘要）
// N = 各启用来源按各自 limit 贡献的求和（与后端合并去重前的上限一致），自定义按实际条目数计
const mixedSummary = computed(() => {
  const parts: string[] = []
  if (props.config.useScore) parts.push(`评分榜(${props.config.scoreLimit})`)
  if (props.config.useOiTop) parts.push(`OI增(${props.config.oiTopLimit})`)
  if (props.config.useOiLow) parts.push(`OI减(${props.config.oiLowLimit})`)
  if (props.config.staticCoins.length > 0) parts.push(`自定义(${props.config.staticCoins.length})`)
  const max =
    (props.config.useScore ? props.config.scoreLimit : 0) +
    (props.config.useOiTop ? props.config.oiTopLimit : 0) +
    (props.config.useOiLow ? props.config.oiLowLimit : 0) +
    props.config.staticCoins.length
  return { max, text: parts.length > 0 ? parts.join(' + ') : '未选择来源' }
})
</script>

<template>
  <div class="coin-editor">
    <!-- 1. 币源交易所 -->
    <div class="block">
      <div class="block-label">币源交易所</div>
      <div class="exchange-cards">
        <div
          v-for="exchange in COIN_SOURCE_EXCHANGES"
          :key="exchange.key"
          class="exchange-card"
          :class="{ active: config.exchange === exchange.key }"
          @click="config.exchange = exchange.key"
        >
          <span class="dot" :style="{ background: exchange.dot }" />
          <div>
            <div class="exchange-name">{{ exchange.label }}</div>
            <div class="exchange-desc">{{ exchange.desc }}</div>
          </div>
        </div>
      </div>
    </div>

    <!-- 2. 数据来源类型 -->
    <div class="block">
      <div class="block-label">数据来源类型</div>
      <div class="source-cards">
        <div
          v-for="source in COIN_SOURCE_CATALOG"
          :key="source.key"
          class="source-card"
          :class="{ active: config.sourceType === source.key }"
          @click="setSourceType(source.key)"
        >
          <IconList v-if="source.key === 'static'" class="source-icon static" />
          <IconStorage v-else-if="source.key === 'score'" class="source-icon gold" />
          <IconArrowRise v-else-if="source.key === 'oi_top'" class="source-icon success" />
          <IconArrowFall v-else-if="source.key === 'oi_low'" class="source-icon danger" />
          <IconLoop v-else class="source-icon primary" />
          <div class="source-name">{{ source.label }}</div>
          <div class="source-desc">{{ source.desc }}</div>
        </div>
      </div>
    </div>

    <!-- 3. 自定义币种（仅静态列表） -->
    <div v-if="config.sourceType === 'static'" class="block">
      <div class="block-label">自定义币种</div>
      <div class="tag-wall">
        <a-tag v-for="coin in config.staticCoins" :key="coin" closable @close="removeCoin(coin)">
          {{ coin }}
        </a-tag>
        <span v-if="config.staticCoins.length === 0" class="empty-inline">暂无币种</span>
      </div>
      <div class="input-row">
        <a-input v-model="coinInput" placeholder="BTC, ETH, SOL..." @press-enter="addCoin" />
        <a-button type="primary" @click="addCoin">
          <template #icon><IconPlus /></template>
          添加币种
        </a-button>
      </div>
    </div>

    <!-- 4. 排除币种 -->
    <div class="block">
      <div class="block-head">
        <IconMinusCircle class="block-icon danger" />
        <span class="block-title">排除币种</span>
      </div>
      <div class="block-desc">这些币种不会被候选池选中，也不参与任何信号计算</div>
      <div class="tag-wall">
        <a-tag v-for="coin in config.excludedCoins" :key="coin" color="red" closable @close="removeExcluded(coin)">
          {{ coin }}
        </a-tag>
        <span v-if="config.excludedCoins.length === 0" class="empty-inline italic">无</span>
      </div>
      <div class="input-row">
        <a-input v-model="excludeInput" placeholder="如 DOGE" @press-enter="addExcluded" />
        <a-button status="danger" @click="addExcluded">添加排除</a-button>
      </div>
    </div>

    <!-- 5. 数据源配置子面板 -->
    <div
      v-if="config.sourceType !== 'static'"
      class="panel"
      :style="{
        borderColor: PANEL_THEME[config.sourceType],
        background: `color-mix(in srgb, ${PANEL_THEME[config.sourceType]} 3%, transparent)`,
      }"
    >
      <!-- 单一来源（score / oi_top / oi_low）：开关行 + 数量上限 + 说明 -->
      <template v-if="config.sourceType !== 'mixed'">
        <div class="panel-row">
          <a-checkbox
            :model-value="isSourceEnabled(config.sourceType as 'score')"
            @update:model-value="(value) => setSourceEnabled(config.sourceType as 'score', value === true)"
          />
          <span class="panel-row-text">启用该来源</span>
          <span class="panel-limit">
            <span class="panel-limit-label">数量上限</span>
            <a-select
              :model-value="sourceLimit(config.sourceType as 'score')"
              :style="{ width: '80px' }"
              @update:model-value="(value) => setSourceLimit(config.sourceType as 'score', Number(value))"
            >
              <a-option v-for="limit in LIMIT_OPTIONS" :key="limit" :value="limit">{{ limit }}</a-option>
            </a-select>
          </span>
        </div>
        <div class="panel-foot-desc">启用后，候选池会并入该来源的币种（上限 N 个）；关闭后该来源不参与本轮决策</div>
      </template>

      <!-- 混合模式：4 张可点卡 + 汇总行 -->
      <template v-else>
        <div class="mixed-cards">
          <div
            v-for="card in mixedCards"
            :key="card.key"
            class="mixed-card"
            :class="{ active: card.checked }"
            :style="{
              borderColor: card.checked ? card.theme : undefined,
              background: card.checked ? `color-mix(in srgb, ${card.theme} 8%, transparent)` : undefined,
            }"
            @click="setSourceEnabled(card.key, !card.checked)"
          >
            <a-checkbox
              :model-value="card.checked"
              @click.stop
              @update:model-value="(value) => setSourceEnabled(card.key, value === true)"
            />
            <div class="mixed-name">{{ card.label }}</div>
            <span class="mixed-limit">
              上限
              <a-select
                :model-value="card.limit"
                :style="{ width: '72px' }"
                @click.stop
                @update:model-value="(value) => setSourceLimit(card.key, Number(value))"
              >
                <a-option v-for="limit in LIMIT_OPTIONS" :key="limit" :value="limit">{{ limit }}</a-option>
              </a-select>
            </span>
          </div>

          <div class="mixed-card custom">
            <div class="mixed-name">自定义</div>
            <div class="mixed-pills">
              <a-tag v-for="coin in config.staticCoins.slice(0, 3)" :key="coin" closable @close="removeCoin(coin)">
                {{ coin }}
              </a-tag>
              <a-tag v-if="config.staticCoins.length > 3">+{{ config.staticCoins.length - 3 }}</a-tag>
              <span v-if="config.staticCoins.length === 0" class="empty-inline">未添加</span>
            </div>
            <div class="mixed-input">
              <a-input v-model="mixedCoinInput" placeholder="BTC, ETH..." size="mini" @press-enter="addMixedCoin" />
              <a-button size="mini" @click="addMixedCoin">
                <template #icon><IconPlus /></template>
              </a-button>
            </div>
          </div>
        </div>

        <div class="mixed-summary">最多 {{ mixedSummary.max }} 个币种 · 组合：{{ mixedSummary.text }}</div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.coin-editor {
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.block {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.block-label {
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}

.block-head {
  display: flex;
  align-items: center;
  gap: 6px;
}

.block-icon.danger {
  color: rgb(var(--danger-6));
  font-size: 16px;
}

.block-title {
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}

.block-desc {
  font-size: 12px;
  color: var(--color-text-3);
}

/* —— 交易所卡 —— */
.exchange-cards {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}

.exchange-card {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 16px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
  cursor: pointer;
  transition: background-color 0.15s, border-color 0.15s;
}

.exchange-card:hover {
  background: var(--color-fill-1);
}

.exchange-card.active {
  border: 2px solid rgba(var(--primary-6), 0.5);
  background: rgba(var(--primary-6), 0.08);
}

.dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  margin-top: 5px;
  flex-shrink: 0;
}

.exchange-name {
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}

.exchange-desc {
  margin-top: 2px;
  font-size: 12px;
  color: var(--color-text-3);
}

/* —— 来源类型卡 —— */
.source-cards {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 8px;
}

.source-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 12px 8px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
  text-align: center;
  cursor: pointer;
  transition: background-color 0.15s, border-color 0.15s;
}

.source-card:hover {
  background: var(--color-fill-1);
}

.source-card.active {
  border-color: rgba(var(--primary-6), 0.6);
  background: rgba(var(--primary-6), 0.08);
}

.source-icon {
  font-size: 24px;
}

.source-icon.static { color: var(--color-text-3); }
.source-icon.gold { color: #f0b90b; }
.source-icon.success { color: rgb(var(--success-6)); }
.source-icon.danger { color: rgb(var(--danger-6)); }
.source-icon.primary { color: rgb(var(--primary-6)); }

.source-name {
  font-size: 14px;
  color: var(--color-text-1);
}

.source-desc {
  font-size: 12px;
  color: var(--color-text-3);
}

/* —— 标签墙与输入行 —— */
.tag-wall {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  min-height: 24px;
  align-items: center;
}

.empty-inline {
  font-size: 12px;
  color: var(--color-text-3);
}

.empty-inline.italic {
  font-style: italic;
}

.input-row {
  display: flex;
  gap: 8px;
}

/* —— 子面板 —— */
.panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 16px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
}

.panel-row {
  display: flex;
  align-items: center;
  gap: 10px;
}

.panel-row-text {
  flex: 1;
  font-size: 14px;
  color: var(--color-text-1);
}

.panel-limit {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.panel-limit-label {
  font-size: 12px;
  color: var(--color-text-3);
}

.panel-foot-desc {
  font-size: 12px;
  color: var(--color-text-3);
}

/* —— 混合模式 —— */
.mixed-cards {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}

.mixed-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
  cursor: pointer;
  transition: border-color 0.15s, background-color 0.15s;
}

.mixed-name {
  font-size: 14px;
  color: var(--color-text-1);
}

.mixed-limit {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--color-text-3);
}

.mixed-pills {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  min-height: 24px;
  align-items: center;
}

.mixed-input {
  display: flex;
  gap: 6px;
}

.mixed-summary {
  padding: 8px;
  border: 1px solid var(--color-border-2);
  border-radius: 4px;
  background: var(--color-bg-1);
  font-size: 12px;
  color: var(--color-text-2);
}
</style>
