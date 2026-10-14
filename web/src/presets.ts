import type { Provider, ProviderType } from './types'

export interface ProviderPreset {
  key: string
  label: string
  type: ProviderType
  baseUrl: string
  model: string
  apiKeyUrl?: string
  icon?: string
  letter?: string
}

export const CUSTOM_PRESET_KEY = 'custom'

export const PROVIDER_PRESETS: ProviderPreset[] = [
  {
    key: 'deepseek',
    label: 'DeepSeek',
    type: 'OpenAICompatible',
    baseUrl: 'https://api.deepseek.com',
    model: 'deepseek-flash',
    apiKeyUrl: 'https://platform.deepseek.com/api_keys',
    icon: '/icons/deepseek.svg',
  },
  {
    key: 'kimi',
    label: 'Kimi（Moonshot）',
    type: 'OpenAICompatible',
    baseUrl: 'https://api.moonshot.cn/v1',
    model: 'kimi-k3',
    apiKeyUrl: 'https://platform.kimi.com/console/api-keys',
    icon: '/icons/kimi.svg',
  },
  {
    key: 'qwen',
    label: '阿里云百炼',
    type: 'OpenAICompatible',
    baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
    model: 'qwen3.7-plus',
    apiKeyUrl: 'https://bailian.console.aliyun.com/?apiKey=1',
    icon: '/icons/qwen.svg',
  },
  {
    key: 'openai',
    label: 'OpenAI',
    type: 'OpenAICompatible',
    baseUrl: 'https://api.openai.com/v1',
    model: 'gpt-5.4-mini',
    apiKeyUrl: 'https://platform.openai.com/api-keys',
    icon: '/icons/openai.svg',
  },
  {
    key: 'glm',
    label: '智谱 GLM',
    type: 'OpenAICompatible',
    baseUrl: 'https://open.bigmodel.cn/api/paas/v4',
    model: 'glm-5.3-flash',
    apiKeyUrl: 'https://open.bigmodel.cn/usercenter/apikeys',
    icon: '/icons/glm.svg',
  },
  {
    key: 'openrouter',
    label: 'OpenRouter',
    type: 'OpenAICompatible',
    baseUrl: 'https://openrouter.ai/api/v1',
    model: 'openai/gpt-5.4-mini',
    apiKeyUrl: 'https://openrouter.ai/settings/keys',
    icon: '/icons/openrouter.svg',
  },
  {
    key: 'siliconflow',
    label: '硅基流动',
    type: 'OpenAICompatible',
    baseUrl: 'https://api.siliconflow.cn/v1',
    model: 'deepseek-ai/DeepSeek-V4-Flash',
    apiKeyUrl: 'https://cloud.siliconflow.cn/account/ak',
    icon: '/icons/siliconflow.svg',
  },
  {
    key: 'ollama',
    label: 'Ollama',
    type: 'Ollama',
    baseUrl: 'http://localhost:11434',
    model: 'qwen3:8b',
    icon: '/icons/ollama.svg',
  },
  {
    key: 'azure',
    label: 'Azure OpenAI',
    type: 'AzureOpenAI',
    baseUrl: '',
    model: '',
    apiKeyUrl: 'https://portal.azure.com/',
    icon: '/icons/azure.svg',
  },
]

export function getPreset(key: string | null): ProviderPreset | undefined {
  return PROVIDER_PRESETS.find((preset) => preset.key === key)
}

function normalizeUrl(url: string): string {
  return url.trim().toLowerCase().replace(/\/+$/, '')
}

// 双向前缀匹配，容忍 /v1 后缀差异（如 provider 填 https://api.deepseek.com/v1）
function matchesPresetUrl(providerUrl: string, presetUrl: string): boolean {
  const a = normalizeUrl(providerUrl)
  const b = normalizeUrl(presetUrl)
  if (!a || !b) return false
  return a === b || a.startsWith(`${b}/`) || b.startsWith(`${a}/`)
}

// Azure 端点无法预置，按类型匹配；其余按类型 + Base URL 匹配
export function matchPreset(provider: Pick<Provider, 'type' | 'baseUrl'>): ProviderPreset | undefined {
  return PROVIDER_PRESETS.find(
    (preset) =>
      preset.type === provider.type &&
      (preset.baseUrl === '' || matchesPresetUrl(provider.baseUrl, preset.baseUrl)),
  )
}

export function isPresetConfigured(providers: Provider[], preset: ProviderPreset): boolean {
  return providers.some((provider) => matchPreset(provider)?.key === preset.key)
}

export function letterOf(name: string): string {
  return name.trim().charAt(0).toUpperCase() || '?'
}
