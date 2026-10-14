<script setup lang="ts">
import { ref, watch } from 'vue'
import { verifyProvider } from '../api'
import type { Provider, VerifyEvent } from '../types'

const props = defineProps<{
  visible: boolean
  provider: Provider | null
}>()

const emit = defineEmits<{ (e: 'update:visible', value: boolean): void }>()

interface CheckState {
  status: 'waiting' | 'running' | 'pass' | 'fail'
  elapsedMs?: number
  detail?: string
  evidence?: string
  count?: number
  ttftMs?: number
}

const CHECKS = [
  { id: 'basic', title: '基础连通', desc: '鉴权有效性与最短对话' },
  { id: 'streaming', title: '流式输出', desc: 'SSE 增量返回（TTFT / chunk 数）' },
  { id: 'tools', title: '工具调用', desc: 'Function Calling' },
  { id: 'json', title: 'JSON 模式', desc: '结构化输出 response_format' },
] as const

type CheckId = (typeof CHECKS)[number]['id']

function createInitialStates(): Record<CheckId, CheckState> {
  return {
    basic: { status: 'waiting' },
    streaming: { status: 'waiting' },
    tools: { status: 'waiting' },
    json: { status: 'waiting' },
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
  const provider = props.provider
  if (!provider) return

  states.value = createInitialStates()
  summary.value = null
  globalError.value = null

  controller?.abort()
  controller = new AbortController()

  try {
    await verifyProvider(provider.id, handleEvent, controller.signal)
  } catch (err) {
    if (!(err instanceof DOMException && err.name === 'AbortError')) {
      globalError.value = err instanceof Error ? err.message : '验证请求失败'
    }
  }
}

function handleEvent(event: VerifyEvent) {
  if (event.type === 'done') {
    summary.value = { passed: event.passed ?? 0, failed: event.failed ?? 0 }
    return
  }
  if (!event.check) return
  const state = states.value[event.check]

  if (event.type === 'start') state.status = 'running'
  else if (event.type === 'chunk') {
    state.count = event.count
    state.ttftMs = event.ttftMs
  } else if (event.type === 'result') {
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
    :title="provider ? `验证：${provider.name}` : '验证'"
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
        <div v-if="check.id === 'streaming' && states[check.id].status === 'running'">
          已接收 {{ states[check.id].count ?? 0 }} 个 chunk<template v-if="states[check.id].ttftMs">
            · 首 token {{ states[check.id].ttftMs }}ms</template>
        </div>
        <div v-if="states[check.id].detail" style="margin-top: 4px">{{ states[check.id].detail }}</div>
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
