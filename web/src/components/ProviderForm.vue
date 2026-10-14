<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { Message } from '@arco-design/web-vue'
import {
  IconCheck,
  IconCode,
  IconDelete,
  IconLaunch,
  IconPlus,
} from '@arco-design/web-vue/es/icon'
import { createProvider, updateProvider } from '../api'
import {
  CUSTOM_PRESET_KEY,
  getPreset,
  isPresetConfigured,
  letterOf,
  matchPreset,
  PROVIDER_PRESETS,
} from '../presets'
import type { ProviderPreset } from '../presets'
import type { Provider, ProviderType, ProviderUpsertRequest, ThinkingMode } from '../types'
import { PROVIDER_TYPE_LABELS } from '../types'

const props = defineProps<{
  visible: boolean
  provider: Provider | null
  providers: Provider[]
}>()

const emit = defineEmits<{
  (e: 'update:visible', value: boolean): void
  (e: 'saved'): void
  (e: 'remove'): void
}>()

const step = ref(0)
const presetKey = ref<string | null>(null)
const submitting = ref(false)

const form = reactive({
  name: '',
  type: 'OpenAICompatible' as ProviderType,
  baseUrl: '',
  apiKey: '',
  model: '',
  apiVersion: '',
  thinkingMode: 'default' as ThinkingMode,
  contextTokens: 128000,
  maxOutputTokens: 16384,
  autoCompressContext: false,
})

const isEdit = computed(() => props.provider !== null)
const isCustom = computed(() => presetKey.value === CUSTOM_PRESET_KEY)
const selectedPreset = computed(() => getPreset(presetKey.value))
const isAzure = computed(() => form.type === 'AzureOpenAI')
const isOllama = computed(() => form.type === 'Ollama')

const canSubmit = computed(
  () => form.name.trim() !== '' && form.baseUrl.trim() !== '' && form.model.trim() !== '',
)

const headerTitle = computed(() => {
  if (isEdit.value) return form.name || '提供商'
  return selectedPreset.value?.label ?? '自定义模型'
})

const headerSub = computed(() => {
  const preset = selectedPreset.value
  if (preset) return `${preset.key.toUpperCase()} • ${form.model || preset.model || '待填写'}`
  if (isCustom.value) return 'custom • OpenAI-compatible'
  return `${PROVIDER_TYPE_LABELS[form.type]} • ${form.model}`
})

const headerIcon = computed(() => {
  const preset = selectedPreset.value
  if (preset?.icon) return { icon: preset.icon, letter: '', plus: false }
  if (preset) return { icon: '', letter: preset.letter ?? letterOf(preset.label), plus: false }
  if (isCustom.value) return { icon: '', letter: '', plus: true }
  return { icon: '', letter: letterOf(form.name), plus: false }
})

const apiKeyPlaceholder = computed(() => {
  if (isEdit.value) return '留空表示不修改'
  if (isOllama.value) return 'Ollama 通常无需 Key'
  return '粘贴 API Key'
})

const THINKING_LABELS: Record<ThinkingMode, string> = {
  default: '默认',
  on: '开启',
  off: '关闭',
}

// 折叠区头部的配置摘要（收起状态也能一眼看到关键配置）
const capabilitySummary = computed(
  () =>
    `思维链 ${THINKING_LABELS[form.thinkingMode]} · 上下文 ${Math.round(form.contextTokens / 1000)}k · 输出 ${
      form.maxOutputTokens > 0 ? `${Math.round(form.maxOutputTokens / 1000)}k` : '默认'
    }`,
)

function resetCapabilities() {
  form.thinkingMode = 'default'
  form.contextTokens = 128000
  form.maxOutputTokens = 16384
  form.autoCompressContext = false
}

watch(
  () => props.visible,
  (visible) => {
    if (!visible) return
    submitting.value = false
    const p = props.provider
    if (p) {
      // 编辑：跳过选择步骤，直接进入配置
      step.value = 1
      presetKey.value = matchPreset(p)?.key ?? null
      form.name = p.name
      form.type = p.type
      form.baseUrl = p.baseUrl
      form.apiKey = ''
      form.model = p.model
      form.apiVersion = p.apiVersion ?? ''
      form.thinkingMode = p.thinkingMode
      form.contextTokens = p.contextTokens
      form.maxOutputTokens = p.maxOutputTokens
      form.autoCompressContext = p.autoCompressContext
    } else {
      step.value = 0
      presetKey.value = null
      form.name = ''
      form.type = 'OpenAICompatible'
      form.baseUrl = ''
      form.apiKey = ''
      form.model = ''
      form.apiVersion = ''
      resetCapabilities()
    }
  },
)

function pickPreset(preset: ProviderPreset) {
  presetKey.value = preset.key
  form.type = preset.type
  form.baseUrl = preset.baseUrl
  form.model = preset.model
  form.name = preset.label
  form.apiKey = ''
  form.apiVersion = ''
  resetCapabilities()
  step.value = 1
}

function pickCustom() {
  presetKey.value = CUSTOM_PRESET_KEY
  form.type = 'OpenAICompatible'
  form.baseUrl = ''
  form.model = ''
  form.name = ''
  form.apiKey = ''
  form.apiVersion = ''
  resetCapabilities()
  step.value = 1
}

// 步骤条回退：仅新增流程允许返回「选择模型」（change 事件回传 1-based 步骤号）
function onStepChange(target: number) {
  if (!isEdit.value && target === 1) step.value = 0
}

async function submit() {
  if (!canSubmit.value) return
  submitting.value = true
  try {
    const payload: ProviderUpsertRequest = {
      name: form.name.trim(),
      type: form.type,
      baseUrl: form.baseUrl.trim(),
      apiKey: form.apiKey.trim() === '' ? null : form.apiKey.trim(),
      model: form.model.trim(),
      apiVersion: form.apiVersion.trim() === '' ? null : form.apiVersion.trim(),
      thinkingMode: form.thinkingMode,
      contextTokens: form.contextTokens,
      maxOutputTokens: form.maxOutputTokens,
      autoCompressContext: form.autoCompressContext,
    }
    if (props.provider) await updateProvider(props.provider.id, payload)
    else await createProvider(payload)
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
  <a-modal :visible="visible" :width="640" :mask-closable="false" @cancel="close">
    <template #title>
      <a-space :size="8">
        <IconCode :style="{ color: 'rgb(var(--primary-6))', fontSize: '20px' }" />
        <span style="font-weight: 600">{{ isEdit ? '编辑提供商' : '添加提供商' }}</span>
      </a-space>
    </template>

    <a-steps
      v-if="!isEdit"
      :current="step + 1"
      small
      changeable
      class="wizard-steps"
      @change="onStepChange"
    >
      <a-step>选择模型</a-step>
      <a-step>配置 API</a-step>
    </a-steps>

    <div v-if="step === 0">
      <div class="section-title">选择模型提供商</div>
      <div class="preset-grid">
        <button
          v-for="preset in PROVIDER_PRESETS"
          :key="preset.key"
          type="button"
          class="preset-card"
          :class="{ 'preset-card-selected': presetKey === preset.key }"
          @click="pickPreset(preset)"
        >
          <div class="preset-icon-wrap">
            <div class="preset-icon">
              <img v-if="preset.icon" :src="preset.icon" :alt="preset.label" />
              <span v-else class="preset-letter">{{ preset.letter ?? letterOf(preset.label) }}</span>
            </div>
            <span v-if="presetKey === preset.key" class="badge badge-selected"><IconCheck /></span>
            <span v-else-if="isPresetConfigured(providers, preset)" class="badge badge-configured">
              <IconCheck />
            </span>
          </div>
          <span class="preset-name">{{ preset.label }}</span>
          <span class="preset-pill">{{ preset.key.toUpperCase() }}</span>
        </button>

        <button type="button" class="preset-card preset-card-custom" @click="pickCustom">
          <div class="preset-icon-wrap">
            <div class="preset-icon"><IconPlus class="preset-plus" /></div>
          </div>
          <span class="preset-name">自定义</span>
          <span class="preset-pill preset-pill-muted">OPENAI-COMPATIBLE</span>
        </button>
      </div>
      <div class="preset-hint">带蓝色标记的提供商已配置</div>
    </div>

    <div v-else>
      <div class="config-header">
        <div class="config-icon">
          <img v-if="headerIcon.icon" :src="headerIcon.icon" :alt="headerTitle" />
          <IconPlus v-else-if="headerIcon.plus" class="preset-plus" />
          <span v-else class="preset-letter">{{ headerIcon.letter }}</span>
        </div>
        <div class="config-meta">
          <div class="config-name">{{ headerTitle }}</div>
          <div class="config-sub">{{ headerSub }}</div>
        </div>
        <a-button
          v-if="selectedPreset?.apiKeyUrl"
          size="small"
          type="outline"
          :href="selectedPreset.apiKeyUrl"
          target="_blank"
          rel="noopener noreferrer"
        >
          <template #icon><IconLaunch /></template>
          获取 API Key
        </a-button>
      </div>

      <div class="field">
        <div class="field-label">名称 *</div>
        <a-input v-model="form.name" placeholder="如：DeepSeek 官方 / 本地 Ollama" />
      </div>
      <div class="field">
        <div class="field-label">{{ isOllama ? 'API 密钥（可留空）' : 'API 密钥' }}</div>
        <a-input-password v-model="form.apiKey" :placeholder="apiKeyPlaceholder" />
      </div>
      <div class="field">
        <div class="field-label">Base URL *</div>
        <a-input
          v-model="form.baseUrl"
          :placeholder="isAzure ? 'https://<你的资源名>.openai.azure.com' : 'https://api.openai.com/v1'"
        />
      </div>
      <div v-if="isAzure" class="field">
        <div class="field-label">API 版本</div>
        <a-input v-model="form.apiVersion" placeholder="如 2024-10-21，留空使用 SDK 默认" />
      </div>
      <div class="field">
        <div class="field-label">模型名 *</div>
        <a-input
          v-model="form.model"
          :placeholder="isAzure ? 'Azure 部署名' : '如 deepseek-flash / qwen3:8b'"
        />
      </div>

      <!-- 模型能力（可折叠，默认收起；头部显示当前配置摘要） -->
      <a-collapse :bordered="false" class="capabilities-collapse">
        <a-collapse-item key="capabilities">
          <template #header>
            <span class="capabilities-title">模型能力</span>
          </template>
          <template #extra>
            <span class="capabilities-summary">{{ capabilitySummary }}</span>
          </template>

          <div class="field">
            <div class="field-label">思维链</div>
            <a-select v-model="form.thinkingMode">
              <a-option value="default">跟随模型默认（不发送参数）</a-option>
              <a-option value="on">开启</a-option>
              <a-option value="off">关闭</a-option>
            </a-select>
            <div class="field-hint">
              开启/关闭会向端点发送思考参数（Ollama：think；OpenAI 兼容 / Azure：enable_thinking）；不支持的端点请保持「跟随默认」
            </div>
          </div>
          <div class="field-row">
            <div class="field">
              <div class="field-label">输入上下文大小（tokens）</div>
              <a-input-number v-model="form.contextTokens" :min="1024" :max="2000000" :step="1024" />
              <div class="field-hint">用于提示词超限估算</div>
            </div>
            <div class="field">
              <div class="field-label">输出上限（tokens）</div>
              <a-input-number v-model="form.maxOutputTokens" :min="0" :max="200000" :step="1024" />
              <div class="field-hint">AI 测试的实际输出上限（0 = 默认 16384）</div>
            </div>
          </div>
          <div class="switch-row">
            <div class="switch-text">
              <div class="switch-name">自动压缩上下文</div>
              <div class="field-hint">提示词超出上下文大小时自动裁剪行情数据（关闭则仅警示）</div>
            </div>
            <a-switch v-model="form.autoCompressContext" />
          </div>
        </a-collapse-item>
      </a-collapse>
    </div>

    <template #footer>
      <div class="modal-footer">
        <div>
          <a-popconfirm v-if="isEdit" content="确定删除该提供商？" @ok="emit('remove')">
            <a-button type="outline" status="danger">
              <template #icon><IconDelete /></template>
              删除
            </a-button>
          </a-popconfirm>
        </div>
        <a-space :size="12">
          <a-button @click="close">取消</a-button>
          <a-button
            v-if="step === 1"
            type="primary"
            :loading="submitting"
            :disabled="!canSubmit"
            @click="submit"
          >
            保存
          </a-button>
        </a-space>
      </div>
    </template>
  </a-modal>
</template>

<style scoped>
.wizard-steps {
  max-width: 320px;
  margin: 0 auto 20px;
}

.section-title {
  font-weight: 600;
  color: var(--color-text-1);
  margin-bottom: 16px;
}

.preset-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
}

.preset-card {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 16px 8px;
  border: 2px solid var(--color-border-2);
  border-radius: 12px;
  background: var(--color-bg-1);
  font-family: inherit;
  cursor: pointer;
  transition: border-color 0.2s, background-color 0.2s;
}

.preset-card:hover {
  border-color: rgb(var(--primary-5));
}

.preset-card-selected {
  border-color: rgb(var(--primary-6));
  background: rgba(var(--primary-6), 0.06);
}

.preset-card-custom {
  border-style: dashed;
  border-color: var(--color-border-3);
}

.preset-icon-wrap {
  position: relative;
}

.preset-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 48px;
  height: 48px;
  border-radius: 12px;
  background: var(--color-fill-2);
}

.preset-icon img {
  width: 32px;
  height: 32px;
}

.preset-letter {
  font-size: 18px;
  font-weight: 700;
  color: #722ed1;
}

.preset-plus {
  font-size: 22px;
  color: var(--color-text-3);
}

.preset-name {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
  white-space: nowrap;
}

.preset-pill {
  padding: 2px 8px;
  border-radius: 100px;
  background: rgba(114, 46, 209, 0.1);
  color: #722ed1;
  font-size: 10px;
  letter-spacing: 0.02em;
  text-transform: uppercase;
}

.preset-pill-muted {
  background: var(--color-fill-2);
  color: var(--color-text-3);
}

.badge {
  position: absolute;
  top: -4px;
  right: -4px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 50%;
  color: #fff;
}

.badge-selected {
  width: 20px;
  height: 20px;
  background: rgb(var(--success-6));
  font-size: 12px;
}

.badge-configured {
  width: 16px;
  height: 16px;
  background: rgb(var(--primary-6));
  font-size: 10px;
}

.preset-hint {
  margin-top: 12px;
  text-align: center;
  font-size: 12px;
  color: var(--color-text-3);
}

.config-header {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px;
  margin-bottom: 16px;
  border: 1px solid var(--color-border-2);
  border-radius: 12px;
  background: var(--color-fill-1);
}

.config-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 48px;
  height: 48px;
  border-radius: 12px;
  background: var(--color-fill-2);
  flex-shrink: 0;
}

.config-icon img {
  width: 32px;
  height: 32px;
}

.config-meta {
  flex: 1;
  min-width: 0;
}

.config-name {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.config-sub {
  font-size: 12px;
  color: var(--color-text-3);
}

.field {
  margin-bottom: 16px;
}

/* —— 模型能力（可折叠区） —— */
.capabilities-collapse {
  border-top: 1px solid var(--color-border-2);
}

.capabilities-collapse :deep(.arco-collapse-item-header) {
  padding-left: 0;
  padding-right: 0;
  background: transparent;
}

.capabilities-collapse :deep(.arco-collapse-item-content) {
  padding-left: 0;
  padding-right: 0;
  background: transparent;
}

.capabilities-title {
  font-weight: 600;
  color: var(--color-text-1);
}

.capabilities-summary {
  font-size: 12px;
  color: var(--color-text-3);
}

.field-hint {
  margin-top: 4px;
  font-size: 12px;
  color: var(--color-text-3);
}

.field-row {
  display: flex;
  gap: 16px;
}

.field-row .field {
  flex: 1;
  min-width: 0;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: 16px;
  margin-bottom: 16px;
}

.switch-text {
  flex: 1;
  min-width: 0;
}

.switch-name {
  font-size: 14px;
  color: var(--color-text-1);
}

.field:last-child {
  margin-bottom: 0;
}

.field-label {
  margin-bottom: 6px;
  font-size: 12px;
  color: var(--color-text-3);
}

.modal-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
</style>
