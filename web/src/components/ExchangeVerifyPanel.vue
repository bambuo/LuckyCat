<script setup lang="ts">
import { ref, watch } from 'vue'
import { verifyExchange } from '../api'
import { exchangeLabel } from '../exchanges'
import type { ExchangeAccount, ExchangeVerifyEvent } from '../types'

const props = defineProps<{
  visible: boolean
  account: ExchangeAccount | null
}>()

const emit = defineEmits<{ (e: 'update:visible', value: boolean): void }>()

interface CheckState {
  status: 'waiting' | 'running' | 'pass' | 'fail'
  elapsedMs?: number
  detail?: string
  evidence?: string
}

const CHECKS = [
  { id: 'connect', title: '行情连通', desc: '公共行情接口（BTC/USDT）' },
  { id: 'auth', title: '账户凭证', desc: '合约账户余额查询验证 API Key / Secret / Passphrase' },
] as const

type CheckId = (typeof CHECKS)[number]['id']

function createInitialStates(): Record<CheckId, CheckState> {
  return {
    connect: { status: 'waiting' },
    auth: { status: 'waiting' },
  }
}

const states = ref(createInitialStates())
const summary = ref<{ passed: number; failed: number } | null>(null)
const globalError = ref<string | null>(null)
let controller: AbortController | null = null

watch(
  () => props.visible,
  (visible) => {
    if (visible) void start()
    else controller?.abort()
  },
)

async function start() {
  const account = props.account
  if (!account) return

  states.value = createInitialStates()
  summary.value = null
  globalError.value = null

  controller?.abort()
  controller = new AbortController()

  try {
    await verifyExchange(account.id, handleEvent, controller.signal)
  } catch (err) {
    if (!(err instanceof DOMException && err.name === 'AbortError')) {
      globalError.value = err instanceof Error ? err.message : '验证请求失败'
    }
  }
}

function handleEvent(event: ExchangeVerifyEvent) {
  if (event.type === 'done') {
    summary.value = { passed: event.passed ?? 0, failed: event.failed ?? 0 }
    return
  }
  if (!event.check) return
  const state = states.value[event.check]

  if (event.type === 'start') state.status = 'running'
  else if (event.type === 'result') {
    state.status = event.status === 'pass' ? 'pass' : 'fail'
    state.elapsedMs = event.elapsedMs
    state.detail = event.detail
    state.evidence = event.evidence
  }
}

function close() {
  emit('update:visible', false)
}
</script>

<template>
  <a-drawer
    :visible="visible"
    width="600px"
    :title="account ? `验证：${exchangeLabel(account.exchangeType)} · ${account.accountName}` : '验证'"
    :footer="false"
    @cancel="close"
  >
    <a-space direction="vertical" fill>
      <a-alert v-if="globalError" type="error">{{ globalError }}</a-alert>
      <a-alert v-if="summary" :type="summary.failed > 0 ? 'warning' : 'success'">
        完成：{{ summary.passed }} 项通过，{{ summary.failed }} 项失败
      </a-alert>

      <a-card v-for="check in CHECKS" :key="check.id" size="small">
        <template #title>
          <a-space>
            <a-tag v-if="states[check.id].status === 'waiting'" color="gray">等待</a-tag>
            <a-tag v-else-if="states[check.id].status === 'running'" color="arcoblue">运行中</a-tag>
            <a-tag v-else-if="states[check.id].status === 'pass'" color="green">通过</a-tag>
            <a-tag v-else color="red">失败</a-tag>
            {{ check.title }}
          </a-space>
        </template>
        <template #extra>
          <span v-if="states[check.id].elapsedMs !== undefined" style="color: #86909c">
            {{ states[check.id].elapsedMs }} ms
          </span>
        </template>

        <div style="color: #86909c; font-size: 12px; margin-bottom: 4px">{{ check.desc }}</div>
        <div v-if="states[check.id].detail" style="margin-top: 4px">
          {{ states[check.id].detail }}
        </div>
        <div
          v-if="states[check.id].evidence"
          style="margin-top: 4px; font-family: ui-monospace, SFMono-Regular, monospace; font-size: 12px; color: #86909c; word-break: break-all"
        >
          证据：{{ states[check.id].evidence }}
        </div>
      </a-card>
    </a-space>
  </a-drawer>
</template>
