<script setup lang="ts">
import { IconExclamationCircle, IconSafe } from '@arco-design/web-vue/es/icon'
import type { RiskControlConfig } from '../../types'

const props = defineProps<{ config: RiskControlConfig }>()

type NumberKey = 'maxPositions' | 'minRiskRewardRatio' | 'minPositionSize' | 'estimatedFeePct' | 'estimatedSlippagePct' | 'estimatedFundingPct' | 'maxEntryDeviationPct'
type SliderKey =
  | 'btcEthLeverage'
  | 'altcoinLeverage'
  | 'btcEthPositionPct'
  | 'altcoinPositionPct'
  | 'maxMarginUsage'
  | 'minConfidence'
  | 'maxRiskPerTradePct'

type Field =
  | {
      kind: 'number'
      key: NumberKey
      label: string
      min: number
      max: number
      step?: number
      width: number
      prefix?: string
      suffix?: string
    }
  | { kind: 'slider'; key: SliderKey; label: string; min: number; max: number; step?: number; suffix: string }

const GROUPS: { title: string; danger?: boolean; fields: Field[] }[] = [
  {
    title: '仓位限制',
    fields: [
      { kind: 'number', key: 'maxPositions', label: '最大持仓数量', min: 1, max: 10, width: 128 },
      { kind: 'slider', key: 'btcEthLeverage', label: 'BTC/ETH 交易杠杆', min: 1, max: 20, suffix: 'x' },
      { kind: 'slider', key: 'altcoinLeverage', label: '山寨币交易杠杆', min: 1, max: 20, suffix: 'x' },
      {
        kind: 'slider',
        key: 'btcEthPositionPct',
        label: '保证金占用上限 · BTC/ETH',
        min: 0.5,
        max: 10,
        step: 0.5,
        suffix: '%',
      },
      {
        kind: 'slider',
        key: 'altcoinPositionPct',
        label: '保证金占用上限 · 其他标的',
        min: 0.5,
        max: 10,
        step: 0.5,
        suffix: '%',
      },
    ],
  },
  {
    title: '风险参数',
    danger: true,
    fields: [
      {
        kind: 'number',
        key: 'minRiskRewardRatio',
        label: '最小成本后风险回报比',
        min: 1,
        max: 10,
        step: 0.5,
        width: 120,
        prefix: '1:',
      },
      { kind: 'slider', key: 'maxMarginUsage', label: '最大保证金使用率', min: 10, max: 100, suffix: '%' },
    ],
  },
  {
    title: '交易成本预算（估算，非实际费率）',
    fields: [
      { kind: 'number', key: 'estimatedFeePct', label: '往返手续费预算', min: 0, max: 10, step: 0.01, width: 120, suffix: '%' },
      { kind: 'number', key: 'estimatedSlippagePct', label: '往返滑点预算', min: 0, max: 10, step: 0.01, width: 120, suffix: '%' },
      { kind: 'number', key: 'estimatedFundingPct', label: '预计支付资金费预算', min: 0, max: 10, step: 0.01, width: 120, suffix: '%' },
      { kind: 'number', key: 'maxEntryDeviationPct', label: '最大执行价偏离', min: 0.01, max: 10, step: 0.01, width: 120, suffix: '%' },
    ],
  },
  {
    title: '开仓要求',
    fields: [
      { kind: 'number', key: 'minPositionSize', label: '最小开仓金额', min: 10, max: 1000, width: 96, suffix: 'USDT' },
      { kind: 'slider', key: 'minConfidence', label: '最小信心度', min: 50, max: 100, suffix: '' },
      { kind: 'slider', key: 'maxRiskPerTradePct', label: '每笔最大风险占比', min: 0.5, max: 5, step: 0.5, suffix: '%' },
    ],
  },
]

function sliderText(field: Extract<Field, { kind: 'slider' }>): string {
  return `${props.config[field.key]}${field.suffix}`
}
</script>

<template>
  <div class="risk-editor">
    <div v-for="group in GROUPS" :key="group.title" class="risk-group">
      <div class="group-head">
        <IconExclamationCircle v-if="group.danger" class="group-icon danger" />
        <IconSafe v-else class="group-icon" />
        <span class="group-title">{{ group.title }}</span>
      </div>
      <div class="group-card">
        <div v-for="field in group.fields" :key="field.key" class="field-row">
          <span class="field-label">{{ field.label }}</span>

          <template v-if="field.kind === 'number'">
            <span class="number-wrap">
              <span v-if="field.prefix" class="number-affix">{{ field.prefix }}</span>
              <a-input-number
                v-model="config[field.key]"
                :min="field.min"
                :max="field.max"
                :step="field.step ?? 1"
                :style="{ width: `${field.width}px` }"
              />
              <span v-if="field.suffix" class="number-affix">{{ field.suffix }}</span>
            </span>
          </template>

          <template v-else>
            <a-slider
              v-model="config[field.key]"
              class="field-slider"
              :min="field.min"
              :max="field.max"
              :step="field.step ?? 1"
              :show-input="false"
            />
            <span class="slider-value">{{ sliderText(field) }}</span>
          </template>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.risk-editor {
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.group-head {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 8px;
}

.group-icon {
  color: rgb(var(--primary-6));
  font-size: 16px;
}

.group-icon.danger {
  color: rgb(var(--danger-6));
}

.group-title {
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
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
  width: 240px;
  flex-shrink: 0;
  font-size: 14px;
  color: var(--color-text-1);
}

.number-wrap {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.number-affix {
  font-size: 12px;
  color: var(--color-text-3);
}

.field-slider {
  flex: 1;
  min-width: 0;
}

.slider-value {
  width: 48px;
  flex-shrink: 0;
  text-align: center;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  color: rgb(var(--primary-6));
}
</style>
