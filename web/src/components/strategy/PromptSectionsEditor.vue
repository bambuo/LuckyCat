<script setup lang="ts">
import { ref } from 'vue'
import { IconCheckCircle, IconClockCircle, IconFile, IconOrderedList, IconUser } from '@arco-design/web-vue/es/icon'
import type { PromptSectionConfig } from '../../types'

const props = defineProps<{ config: PromptSectionConfig; defaults: PromptSectionConfig }>()

const SECTIONS = [
  { key: 'role', label: '角色定义', desc: '定义 AI 的身份和核心目标', icon: IconUser },
  { key: 'frequency', label: '交易频率', desc: '设定交易频率预期和过度交易警告', icon: IconClockCircle },
  { key: 'entry', label: '开仓标准', desc: '定义开仓信号条件和避免事项', icon: IconCheckCircle },
  { key: 'workflow', label: '决策流程', desc: '设定决策步骤和思考流程', icon: IconOrderedList },
] as const

type SectionKey = (typeof SECTIONS)[number]['key']

// 初始全部折叠（设计稿 §4.5）；标识 = vnode key（Arco 折叠面板以 key 匹配 activeKey）
const activeKeys = ref<(string | number)[]>([])

// 「已修改」= 存值非空（覆盖了内置默认）；重置 = 置空回退内置默认
function isModified(key: SectionKey): boolean {
  return props.config[key].trim() !== ''
}

function currentValue(key: SectionKey): string {
  return props.config[key] || props.defaults[key]
}
</script>

<template>
  <div class="prompt-editor">
    <div class="editor-head">
      <IconFile class="head-icon" />
      <div>
        <div class="head-title">系统提示词自定义</div>
        <div class="head-desc">自定义 AI 行为和决策逻辑；输出格式和风控规则不可修改，由配置自动生成</div>
      </div>
    </div>

    <a-collapse
      v-model:active-key="activeKeys"
      :bordered="false"
      expand-icon-position="right"
      class="prompt-sections"
    >
      <a-collapse-item v-for="section in SECTIONS" :key="section.key">
        <template #header>
          <span class="section-head">
            <component :is="section.icon" class="section-icon" aria-hidden="true" />
            <span class="section-label">{{ section.label }}</span>
            <a-tag v-if="isModified(section.key)" color="gold" size="small">已修改</a-tag>
          </span>
        </template>
        <template #extra>
          <span class="char-count">{{ currentValue(section.key).length }} 字符</span>
        </template>
        <div class="section-desc">{{ section.desc }}</div>
        <a-textarea
          :model-value="currentValue(section.key)"
          class="mono section-text"
          :auto-size="{ minRows: 6 }"
          @update:model-value="(value: string) => (config[section.key] = value)"
        />
        <div class="section-foot">
          <a-button size="small" :disabled="!isModified(section.key)" @click="config[section.key] = ''">
            重置为默认
          </a-button>
        </div>
      </a-collapse-item>
    </a-collapse>
  </div>
</template>

<style scoped>
.prompt-editor {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.editor-head {
  display: flex;
  align-items: flex-start;
  gap: 8px;
}

.head-icon {
  margin-top: 2px;
  font-size: 20px;
  color: #a855f7;
}

.head-title {
  font-size: 14px;
  font-weight: 500;
  color: var(--color-text-1);
}

.head-desc {
  margin-top: 2px;
  font-size: 12px;
  color: var(--color-text-3);
}

/* 注意：不要覆盖折叠头部的 padding-right——Arco 在「展开图标置右」时用 34px 右内边距
   为绝对定位的图标让位，字符数（float: right）依赖该内边距避免与其重叠 */

.section-head {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.section-label {
  font-size: 14px;
  color: var(--color-text-1);
}

.section-icon {
  flex-shrink: 0;
  font-size: 16px;
  color: #a855f7;
}

.char-count {
  font-size: 10px;
  color: var(--color-text-3);
}

.section-desc {
  margin-bottom: 6px;
  font-size: 12px;
  color: var(--color-text-3);
}

.mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
}

.section-text {
  resize: vertical;
}

.section-foot {
  display: flex;
  justify-content: flex-end;
  margin-top: 8px;
}
</style>
