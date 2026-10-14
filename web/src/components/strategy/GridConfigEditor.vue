<script setup lang="ts">
import { IconThunderbolt } from '@arco-design/web-vue/es/icon'
import { GRID_DISTRIBUTIONS, GRID_SYMBOLS } from '../../strategy'
import type { GridConfig } from '../../types'

const props = defineProps<{ config: GridConfig }>()

const DIRECTION_MODES = [
  '中性（50%）：买单 50% / 卖单 50%',
  '偏多（X%）：买单 X% / 卖单（100−X）%',
  '全多（100%）：买单 100% / 卖单 0%',
  '偏空（X%）：卖单 X% / 买单（100−X）%',
  '全空（100%）：卖单 100% / 买单 0%',
]

function biasPercent(): number {
  return Math.round(props.config.directionBiasRatio * 100)
}

// 说明盒与公式卡的示例数值跟随滑杆（设计稿：55–90，步进 5，示例 X = 70%）
function exampleBuy(): number {
  return Math.max(55, Math.min(90, biasPercent() || 70))
}

// 滑杆回写四舍五入到两位小数，避免浮点尾数入库（如 0.7000000000000001）
function setBias(value: number | [number, number]) {
  const single = Array.isArray(value) ? value[0] : value
  props.config.directionBiasRatio = Math.round(single * 100) / 100
}
</script>

<template>
  <div class="grid-editor">
    <!-- 1. 交易设置 -->
    <div class="group">
      <div class="group-head"><IconThunderbolt class="group-icon" />交易设置</div>
      <div class="group-card">
        <div class="field-row">
          <span class="field-label">交易对</span>
          <a-select v-model="config.symbol" :style="{ width: '160px' }">
            <a-option v-for="symbol in GRID_SYMBOLS" :key="symbol" :value="symbol">{{ symbol }}</a-option>
          </a-select>
        </div>
        <div class="field-row">
          <span class="field-label">投资金额</span>
          <span class="number-wrap">
            <a-input-number v-model="config.totalInvestment" :min="100" :step="100" :style="{ width: '160px' }" />
            <span class="affix">USDT</span>
          </span>
        </div>
        <div class="field-row">
          <span class="field-label">杠杆</span>
          <a-input-number v-model="config.leverage" :min="1" :max="5" :style="{ width: '160px' }" />
        </div>
      </div>
    </div>

    <!-- 2. 网格参数 -->
    <div class="group">
      <div class="group-head"><IconThunderbolt class="group-icon" />网格参数</div>
      <div class="group-card">
        <div class="field-row">
          <span class="field-label">网格数量</span>
          <a-input-number v-model="config.gridCount" :min="5" :max="50" :style="{ width: '160px' }" />
        </div>
        <div class="field-row">
          <span class="field-label">资金分配</span>
          <a-select v-model="config.distribution" :style="{ width: '220px' }">
            <a-option v-for="item in GRID_DISTRIBUTIONS" :key="item.key" :value="item.key">{{ item.label }}</a-option>
          </a-select>
        </div>
      </div>
    </div>

    <!-- 3. 价格边界 -->
    <div class="group">
      <div class="group-head"><IconThunderbolt class="group-icon" />价格边界</div>
      <div class="group-card">
        <div class="field-row">
          <span class="field-label">自动计算边界（ATR）</span>
          <a-switch v-model="config.useAtrBounds" />
        </div>
        <div v-if="config.useAtrBounds" class="field-row">
          <span class="field-label">ATR 倍数</span>
          <a-input-number v-model="config.atrMultiplier" :min="1" :max="5" :step="0.5" :style="{ width: '160px' }" />
        </div>
        <template v-else>
          <div class="field-row">
            <span class="field-label">上边界</span>
            <span class="number-wrap">
              <a-input-number v-model="config.upperPrice" :min="0" :step="0.01" :style="{ width: '160px' }" />
              <span class="affix">USDT</span>
            </span>
          </div>
          <div class="field-row">
            <span class="field-label">下边界</span>
            <span class="number-wrap">
              <a-input-number v-model="config.lowerPrice" :min="0" :step="0.01" :style="{ width: '160px' }" />
              <span class="affix">USDT</span>
            </span>
          </div>
        </template>
      </div>
    </div>

    <!-- 4. 风险控制 -->
    <div class="group">
      <div class="group-head"><IconThunderbolt class="group-icon" />风险控制</div>
      <div class="group-card">
        <div class="field-row">
          <span class="field-label">最大回撤 %</span>
          <a-input-number v-model="config.maxDrawdownPct" :min="5" :max="50" :style="{ width: '160px' }" />
        </div>
        <div class="field-row">
          <span class="field-label">止损 %</span>
          <a-input-number v-model="config.stopLossPct" :min="1" :max="20" :style="{ width: '160px' }" />
        </div>
        <div class="field-row">
          <span class="field-label">日损失限制 %</span>
          <a-input-number v-model="config.dailyLossLimitPct" :min="1" :max="30" :style="{ width: '160px' }" />
        </div>
        <div class="field-row">
          <span class="field-label">仅使用 Maker 订单</span>
          <a-switch v-model="config.useMakerOnly" />
        </div>
      </div>
    </div>

    <!-- 5. 方向自动调整 -->
    <div class="group">
      <div class="group-head"><IconThunderbolt class="group-icon" />方向自动调整</div>
      <div class="group-card">
        <div class="field-row">
          <span class="field-label">启用方向调整</span>
          <a-switch v-model="config.enableDirectionAdjust" />
        </div>

        <template v-if="config.enableDirectionAdjust">
          <!-- 方向模式说明盒（设计稿 §4.1：蓝边、5 条模式、结论行） -->
          <div class="mode-box">
            <div class="mode-title">方向模式说明</div>
            <ul class="mode-list">
              <li v-for="mode in DIRECTION_MODES" :key="mode">{{ mode }}</li>
            </ul>
            <div class="mode-foot">
              偏向强度决定买卖侧权重倾斜：偏多时买单权重随强度提高，偏空时相反；中性值 50 表示完全均衡。
            </div>
          </div>

          <div class="field-row">
            <span class="field-label">偏向强度</span>
            <a-slider
              :model-value="config.directionBiasRatio"
              class="bias-slider"
              :min="0.55"
              :max="0.9"
              :step="0.05"
              :show-input="false"
              @update:model-value="setBias"
            />
            <span class="bias-value">X = {{ biasPercent() }}%</span>
          </div>

          <!-- 两色公式卡 -->
          <div class="formula-cards">
            <div class="formula-card up">
              <div class="formula-title">偏多示例（X = {{ exampleBuy() }}%）</div>
              <div class="formula-body">买单权重 {{ exampleBuy() }}% / 卖单权重 {{ 100 - exampleBuy() }}%</div>
            </div>
            <div class="formula-card down">
              <div class="formula-title">偏空示例（X = {{ exampleBuy() }}%）</div>
              <div class="formula-body">卖单权重 {{ exampleBuy() }}% / 买单权重 {{ 100 - exampleBuy() }}%</div>
            </div>
          </div>
        </template>
      </div>
    </div>
  </div>
</template>

<style scoped>
.grid-editor {
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.group-head {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 8px;
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}

.group-icon {
  font-size: 16px;
  color: rgb(var(--primary-6));
}

.group-card {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 16px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
}

.field-row {
  display: flex;
  align-items: center;
  gap: 12px;
}

.field-label {
  width: 180px;
  flex-shrink: 0;
  font-size: 14px;
  color: var(--color-text-1);
}

.number-wrap {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.affix {
  font-size: 12px;
  color: var(--color-text-3);
}

.mode-box {
  padding: 16px;
  border: 1px solid rgba(var(--primary-6), 0.5);
  border-radius: 8px;
  background: var(--color-bg-2);
}

.mode-title {
  margin-bottom: 8px;
  font-size: 12px;
  color: rgb(var(--primary-6));
}

.mode-list {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 4px 16px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.mode-list li {
  font-size: 12px;
  color: var(--color-text-3);
}

.mode-foot {
  margin-top: 10px;
  padding-top: 10px;
  border-top: 1px solid var(--color-border-2);
  font-size: 12px;
  color: var(--color-text-3);
}

.bias-slider {
  flex: 1;
  min-width: 0;
}

.bias-value {
  width: 80px;
  flex-shrink: 0;
  text-align: right;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 14px;
  color: rgb(var(--primary-6));
}

.formula-cards {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}

.formula-card {
  padding: 12px;
  border-radius: 8px;
}

.formula-card.up {
  border: 1px solid rgba(var(--success-6), 0.2);
  background: rgba(var(--success-6), 0.05);
}

.formula-card.down {
  border: 1px solid rgba(var(--danger-6), 0.2);
  background: rgba(var(--danger-6), 0.05);
}

.formula-title {
  font-size: 12px;
  font-weight: 500;
  color: var(--color-text-1);
}

.formula-body {
  margin-top: 4px;
  font-size: 12px;
  color: var(--color-text-3);
}
</style>
