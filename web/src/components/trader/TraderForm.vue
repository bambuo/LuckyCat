<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconInfoCircle } from '@arco-design/web-vue/es/icon'
import { createTrader, getExchangeBalance, updateTrader } from '../../api'
import type { ExchangeAccount, Provider, Strategy, Trader, TraderExecutionMode, TraderUpsertRequest } from '../../types'

const props = defineProps<{
  visible: boolean
  trader: Trader | null
  providers: Provider[]
  accounts: ExchangeAccount[]
  strategies: Strategy[]
}>()

const emit = defineEmits<{
  (e: 'update:visible', value: boolean): void
  (e: 'saved'): void
}>()

const submitting = ref(false)
const balanceLoading = ref(false)
const balanceError = ref<string | null>(null)

const form = reactive({
  name: '',
  providerId: null as number | null,
  exchangeAccountId: null as number | null,
  strategyId: null as number | null,
  isCrossMargin: true,
  scanIntervalMinutes: 3,
  initialBalance: null as number | null,
  executionMode: 'live' as TraderExecutionMode,
})

const isEdit = computed(() => props.trader !== null)
const selectedStrategy = computed(() => props.strategies.find((s) => s.id === form.strategyId) ?? null)

/** 后端金额字符串 → 表单数字模型；空串或非法值为 null。 */
function toFormNumber(value: string | null): number | null {
  if (value === null || value.trim() === '') return null
  const parsed = Number(value)
  return Number.isFinite(parsed) ? parsed : null
}

const strategyDetail = computed(() => {
  const s = selectedStrategy.value
  if (!s) return null
  return {
    active: s.isActive,
    description: s.description ?? '',
    exchange: (s.config.coinSource.exchange ?? 'binance').toUpperCase(),
    marginUsage: s.config.riskControl.maxMarginUsage,
  }
})

const canSubmit = computed(
  () =>
    !submitting.value &&
    form.name.trim() !== '' &&
    form.providerId !== null &&
    form.exchangeAccountId !== null &&
    form.strategyId !== null,
)

watch(
  () => props.visible,
  (visible) => {
    if (!visible) return
    submitting.value = false
    balanceError.value = null
    const t = props.trader
    if (t) {
      form.name = t.name
      form.providerId = t.providerId
      form.exchangeAccountId = t.exchangeAccountId
      form.strategyId = t.strategyId
      form.isCrossMargin = t.isCrossMargin
      form.scanIntervalMinutes = t.scanIntervalMinutes
      // 后端金额为字符串（LC-STD-03 §3.4）：表单内部用数字模型给 a-input-number，提交时再转字符串。
      form.initialBalance = toFormNumber(t.initialBalance)
      form.executionMode = t.executionMode
    } else {
      const active = props.strategies.find((s) => s.isActive)
      form.name = ''
      form.providerId = props.providers[0]?.id ?? null
      form.exchangeAccountId = props.accounts[0]?.id ?? null
      form.strategyId = active?.id ?? props.strategies[0]?.id ?? null
      form.isCrossMargin = true
      form.scanIntervalMinutes = 3
      form.initialBalance = null
      form.executionMode = 'live'
    }
  },
)

async function fetchBalance() {
  if (form.exchangeAccountId === null || balanceLoading.value) return
  balanceLoading.value = true
  balanceError.value = null
  try {
    const result = await getExchangeBalance(form.exchangeAccountId)
    form.initialBalance = Math.round(result.usdt * 100) / 100
    Message.success('已读取账户 USDT 余额')
  } catch (err) {
    balanceError.value = err instanceof Error ? err.message : '余额查询失败'
  } finally {
    balanceLoading.value = false
  }
}

async function submit() {
  if (!canSubmit.value) return
  submitting.value = true
  const payload: TraderUpsertRequest = {
    name: form.name.trim(),
    providerId: form.providerId!,
    exchangeAccountId: form.exchangeAccountId!,
    strategyId: form.strategyId!,
    isCrossMargin: form.isCrossMargin,
    scanIntervalMinutes: form.scanIntervalMinutes,
    initialBalance: form.initialBalance === null ? null : String(form.initialBalance),
    executionMode: form.executionMode,
  }
  try {
    if (props.trader) await updateTrader(props.trader.id, payload)
    else await createTrader(payload)
    Message.success('已保存')
    emit('saved')
    emit('update:visible', false)
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '保存失败')
  } finally {
    submitting.value = false
  }
}

function close() {
  emit('update:visible', false)
}
</script>

<template>
  <a-modal
    :visible="visible"
    :width="640"
    :footer="false"
    :mask-closable="false"
    esc-to-close
    @cancel="close"
  >
    <template #title>{{ isEdit ? '编辑交易员' : '创建交易员' }}</template>

    <div class="trader-form">
      <!-- ① 基础配置 -->
      <div class="section-title">① 基础配置</div>
      <div class="field">
        <div class="field-label">交易员名称 <span class="required">*</span></div>
        <a-input v-model="form.name" placeholder="输入交易员名称" allow-clear />
      </div>
      <div class="field-row">
        <div class="field">
          <div class="field-label">选择 AI 模型 <span class="required">*</span></div>
          <a-select :model-value="form.providerId ?? undefined" placeholder="选择模型" @update:model-value="(v) => (form.providerId = (v as number) ?? null)">
            <a-option v-for="p in providers" :key="p.id" :value="p.id">{{ p.name }}</a-option>
          </a-select>
        </div>
        <div class="field">
          <div class="field-label">选择交易所 <span class="required">*</span></div>
          <a-select :model-value="form.exchangeAccountId ?? undefined" placeholder="选择交易所" @update:model-value="(v) => (form.exchangeAccountId = (v as number) ?? null)">
            <a-option v-for="a in accounts" :key="a.id" :value="a.id">
              {{ a.exchangeType.toUpperCase() }} - {{ a.accountName }}
            </a-option>
          </a-select>
        </div>
      </div>

      <!-- ② 选择交易策略 -->
      <div class="section-title">② 选择交易策略</div>
      <div class="field">
        <div class="field-label">使用策略 <span class="required">*</span></div>
        <a-select :model-value="form.strategyId ?? undefined" placeholder="请选择策略（必填）" @update:model-value="(v) => (form.strategyId = (v as number) ?? null)">
          <a-option v-for="s in strategies" :key="s.id" :value="s.id">
            {{ s.name }}{{ s.isActive ? ' (当前激活)' : '' }}
          </a-option>
        </a-select>
        <div v-if="strategies.length === 0" class="field-hint warn">
          暂无策略，请先在策略工作室创建并启用策略后再新建交易员
        </div>
      </div>
      <div v-if="strategyDetail" class="strategy-card">
        <div class="strategy-head">
          <span class="strategy-name">策略详情</span>
          <a-tag v-if="strategyDetail.active" color="green" size="small">已启用</a-tag>
        </div>
        <div v-if="strategyDetail.description" class="strategy-desc">{{ strategyDetail.description }}</div>
        <div class="strategy-kv">
          <span class="kv-label">币种来源</span>
          <span class="kv-value">{{ strategyDetail.exchange }}</span>
          <span class="kv-label">保证金上限</span>
          <span class="kv-value">{{ strategyDetail.marginUsage }}%</span>
        </div>
      </div>

      <!-- ③ 交易参数 -->
      <div class="section-title">③ 交易参数</div>
      <div class="field">
        <div class="field-label">执行模式</div>
        <div class="segmented">
          <a-button
            size="small"
            :type="form.executionMode === 'live' ? 'primary' : 'outline'"
            @click="form.executionMode = 'live'"
          >
            实盘
          </a-button>
          <a-button
            size="small"
            :type="form.executionMode === 'dry' ? 'primary' : 'outline'"
            @click="form.executionMode = 'dry'"
          >
            演练
          </a-button>
        </div>
        <div v-if="form.executionMode === 'live'" class="field-hint danger">
          实盘将以真实资金自动下单；启动前会校验交易所下单能力，无法附加止损时拒绝开仓
        </div>
        <div v-else class="field-hint">演练模式只运行 AI 决策与审计留痕，不下任何真实订单</div>
      </div>
      <div class="field-row">
        <div class="field">
          <div class="field-label">保证金模式</div>
          <div class="segmented">
            <a-button
              size="small"
              :type="form.isCrossMargin ? 'primary' : 'outline'"
              @click="form.isCrossMargin = true"
            >
              全仓
            </a-button>
            <a-button
              size="small"
              :type="!form.isCrossMargin ? 'primary' : 'outline'"
              @click="form.isCrossMargin = false"
            >
              逐仓
            </a-button>
          </div>
        </div>
        <div class="field">
          <div class="field-label">AI 扫描决策间隔（分钟）</div>
          <a-input-number
            v-model="form.scanIntervalMinutes"
            :min="3"
            :max="60"
            :step="1"
            style="width: 100%"
          />
          <div class="field-hint">范围 3–60 分钟，启动后立即执行一轮</div>
        </div>
      </div>
      <div class="field">
        <div class="field-label">初始余额（$）</div>
        <div class="balance-row">
          <a-input-number
            :model-value="form.initialBalance ?? undefined"
            :min="0"
            :step="0.01"
            placeholder="可留空，或读取账户余额"
            style="flex: 1"
            @update:model-value="(v) => (form.initialBalance = typeof v === 'number' ? v : null)"
          />
          <a-button size="small" :loading="balanceLoading" @click="fetchBalance">
            {{ balanceLoading ? '获取中...' : '获取当前余额' }}
          </a-button>
        </div>
        <div v-if="balanceError" class="field-hint danger">{{ balanceError }}</div>
        <div v-else class="field-hint">
          <IconInfoCircle class="hint-icon" />读取所选交易所账户的真实 USDT 余额作为初始净值基准
        </div>
      </div>

      <div class="form-footer">
        <a-button @click="close">取消</a-button>
        <a-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
          {{ submitting ? '正在保存…' : isEdit ? '保存修改' : '创建交易员' }}
        </a-button>
      </div>
    </div>
  </a-modal>
</template>

<style scoped>
.trader-form {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.section-title {
  font-size: 12px;
  font-weight: 600;
  color: rgb(var(--primary-6));
  padding-bottom: 6px;
  border-bottom: 1px solid var(--color-border-1);
  margin-top: 4px;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  flex: 1;
  min-width: 0;
}

.field-row {
  display: flex;
  gap: 16px;
}

.field-label {
  font-size: 12px;
  color: var(--color-text-2);
}

.required {
  color: rgb(var(--danger-6));
}

.field-hint {
  font-size: 12px;
  color: var(--color-text-3);
  display: flex;
  align-items: center;
  gap: 4px;
}

.field-hint.warn {
  color: rgb(var(--warning-6));
}

.field-hint.danger {
  color: rgb(var(--danger-6));
}

.hint-icon {
  font-size: 13px;
}

.segmented {
  display: flex;
  gap: 8px;
}

.strategy-card {
  background: var(--color-fill-1);
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.strategy-head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.strategy-name {
  font-size: 13px;
  font-weight: 600;
  color: rgb(var(--primary-6));
}

.strategy-desc {
  font-size: 12px;
  color: var(--color-text-2);
}

.strategy-kv {
  display: grid;
  grid-template-columns: auto 1fr auto 1fr;
  gap: 4px 8px;
  font-size: 12px;
}

.kv-label {
  color: var(--color-text-3);
}

.kv-value {
  color: var(--color-text-1);
}

.balance-row {
  display: flex;
  gap: 8px;
  align-items: center;
}

.form-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding-top: 14px;
  border-top: 1px solid var(--color-border-1);
  margin-top: 4px;
}
</style>
