<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { IconDelete } from '@arco-design/web-vue/es/icon'
import { createExchange, updateExchange } from '../api'
import { EXCHANGE_PRESETS, exchangeLetter, getExchangePreset } from '../exchanges'
import type { ExchangeAccount, ExchangeType, ExchangeUpsertRequest } from '../types'

const props = defineProps<{
  visible: boolean
  account: ExchangeAccount | null
  accounts: ExchangeAccount[]
}>()

const emit = defineEmits<{
  (e: 'update:visible', value: boolean): void
  (e: 'saved'): void
  (e: 'remove', account: ExchangeAccount): void
}>()

const step = ref(0)
const selected = ref<ExchangeType | null>(null)
const submitting = ref(false)
const error = ref<string | null>(null)

const form = reactive({
  accountName: '',
  apiKey: '',
  apiSecret: '',
  passphrase: '',
  userId: '',
})

const isEdit = computed(() => props.account !== null)
const preset = computed(() => (selected.value ? getExchangePreset(selected.value) : undefined))
const needsPassphrase = computed(() => preset.value?.needsPassphrase ?? false)
const needsUserId = computed(() => preset.value?.needsUserId ?? false)
const configuredTypes = computed(() => new Set(props.accounts.map((a) => a.exchangeType)))

const canSubmit = computed(() => {
  if (!form.accountName.trim()) return false
  if (isEdit.value) return true
  if (!form.apiKey.trim() || !form.apiSecret.trim()) return false
  if (needsPassphrase.value && !form.passphrase.trim()) return false
  return true
})

watch(
  () => props.visible,
  (visible) => {
    if (!visible) return
    error.value = null
    submitting.value = false
    const account = props.account
    if (account) {
      step.value = 1
      selected.value = account.exchangeType
      form.accountName = account.accountName
      form.apiKey = ''
      form.apiSecret = ''
      form.passphrase = ''
      form.userId = account.userId ?? ''
    } else {
      step.value = 0
      selected.value = null
      form.accountName = ''
      form.apiKey = ''
      form.apiSecret = ''
      form.passphrase = ''
      form.userId = ''
    }
  },
)

function pick(exchangeType: ExchangeType) {
  selected.value = exchangeType
  step.value = 1
}

// Arco Steps 为 1-based；仅允许从第 2 步回退到第 1 步
function onStepChange(current: number) {
  if (current === 1 && !isEdit.value) step.value = 0
}

async function submit() {
  if (!selected.value || !canSubmit.value) return
  submitting.value = true
  error.value = null
  const payload: ExchangeUpsertRequest = {
    exchangeType: selected.value,
    accountName: form.accountName.trim(),
    apiKey: form.apiKey.trim() || null,
    apiSecret: form.apiSecret.trim() || null,
    passphrase: form.passphrase.trim() || null,
    userId: form.userId.trim() || null,
  }
  try {
    if (props.account) await updateExchange(props.account.id, payload)
    else await createExchange(payload)
    emit('saved')
    close()
  } catch (err) {
    error.value = err instanceof Error ? err.message : '保存失败'
  } finally {
    submitting.value = false
  }
}

function close() {
  emit('update:visible', false)
}

function removeFromForm() {
  if (props.account) emit('remove', props.account)
}
</script>

<template>
  <a-modal
    :visible="visible"
    :width="680"
    :title="isEdit ? '编辑交易所账户' : '添加交易所账户'"
    :footer="false"
    unmount-on-close
    @cancel="close"
  >
    <a-steps v-if="!isEdit" :current="step + 1" class="ex-steps" @change="onStepChange">
      <a-step>选择交易所</a-step>
      <a-step>配置账户</a-step>
    </a-steps>

    <!-- 第 1 步：选择交易所 -->
    <div v-if="!isEdit && step === 0" class="ex-grid">
      <button
        v-for="item in EXCHANGE_PRESETS"
        :key="item.key"
        type="button"
        class="ex-card"
        :class="{ 'ex-card-selected': selected === item.key }"
        @click="pick(item.key)"
      >
        <span v-if="configuredTypes.has(item.key)" class="ex-configured">已配置</span>
        <span class="ex-icon">
          <img v-if="item.icon" :src="item.icon" :alt="item.label" />
          <span v-else class="ex-letter">{{ exchangeLetter(item.key) }}</span>
        </span>
        <span class="ex-label">{{ item.label }}</span>
      </button>
    </div>

    <!-- 第 2 步 / 编辑态：配置账户 -->
    <template v-else>
      <div v-if="preset" class="ex-head">
        <span class="ex-head-icon">
          <img v-if="preset.icon" :src="preset.icon" :alt="preset.label" />
          <span v-else class="ex-letter">{{ exchangeLetter(preset.key) }}</span>
        </span>
        <div>
          <div class="ex-head-name">{{ preset.label }}</div>
          <div class="ex-head-sub">
            {{ needsPassphrase ? '需要 API Key、Secret 与 Passphrase' : '需要 API Key 与 Secret' }}
          </div>
        </div>
      </div>

      <a-alert v-if="error" type="error" style="margin-bottom: 12px">{{ error }}</a-alert>

      <a-form :model="form" layout="vertical">
        <a-form-item label="账户名称" required>
          <a-input v-model="form.accountName" placeholder="如 主账户" allow-clear />
        </a-form-item>
        <a-form-item label="API Key">
          <a-input-password
            v-model="form.apiKey"
            :placeholder="isEdit ? '留空保持原值' : '粘贴 API Key'"
            allow-clear
          />
        </a-form-item>
        <a-form-item label="API Secret">
          <a-input-password
            v-model="form.apiSecret"
            :placeholder="isEdit ? '留空保持原值' : '粘贴 API Secret'"
            allow-clear
          />
        </a-form-item>
        <a-form-item v-if="needsPassphrase" label="口令 Passphrase">
          <a-input-password
            v-model="form.passphrase"
            :placeholder="isEdit ? '留空保持原值' : '创建 API Key 时设置的口令'"
            allow-clear
          />
        </a-form-item>
        <a-form-item v-if="needsUserId" label="用户 ID（UID）">
          <a-input v-model="form.userId" placeholder="Gate.io 数字 UID（API 管理页可见）" allow-clear />
          <template #extra>
            <span class="ex-hint">持仓 / 余额的实时推送（WebSocket）签名需要；只做 REST 查询可留空</span>
          </template>
        </a-form-item>
      </a-form>

      <div class="ex-foot">
        <div>
          <a-button v-if="isEdit" status="danger" type="outline" @click="removeFromForm">
            <template #icon><IconDelete /></template>
            删除
          </a-button>
        </div>
        <a-space>
          <a-button v-if="!isEdit" @click="step = 0">上一步</a-button>
          <a-button type="primary" :disabled="!canSubmit" :loading="submitting" @click="submit">
            {{ submitting ? '保存中…' : '保存' }}
          </a-button>
        </a-space>
      </div>
    </template>
  </a-modal>
</template>

<style scoped>
.ex-steps {
  max-width: 320px;
  margin: 8px auto 20px;
}

.ex-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 12px;
  padding: 4px 0 8px;
}

.ex-card {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 20px 12px;
  border: 1px solid var(--color-border-2);
  border-radius: 10px;
  background: var(--color-bg-1);
  font: inherit;
  cursor: pointer;
  transition: border-color 0.15s, background-color 0.15s;
}

.ex-card:hover {
  border-color: rgb(var(--primary-6));
  background: var(--color-fill-1);
}

.ex-card-selected {
  border-color: rgb(var(--primary-6));
  box-shadow: 0 0 0 1px rgb(var(--primary-6));
}

.ex-configured {
  position: absolute;
  top: 6px;
  right: 8px;
  font-size: 11px;
  color: #00b42a;
}

.ex-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 48px;
  height: 48px;
}

.ex-icon img {
  width: 44px;
  height: 44px;
  border-radius: 8px;
}

.ex-letter {
  font-size: 20px;
  font-weight: 700;
  color: var(--color-text-2);
}

.ex-label {
  font-size: 13px;
  font-weight: 500;
  color: var(--color-text-1);
}

.ex-head {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px;
  margin-bottom: 16px;
  border: 1px solid var(--color-border-2);
  border-radius: 10px;
  background: var(--color-fill-1);
}

.ex-head-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
}

.ex-head-icon img {
  width: 32px;
  height: 32px;
  border-radius: 6px;
}

.ex-head-name {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.ex-head-sub {
  font-size: 12px;
  color: var(--color-text-3);
}

.ex-hint {
  font-size: 12px;
  color: var(--color-text-3);
}

.ex-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 8px;
}
</style>
