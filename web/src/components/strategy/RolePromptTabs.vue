<script setup lang="ts">
import type { PromptSectionConfig, RoleInstructions, StrategyConfig } from '../../types'
import PromptSectionsEditor from './PromptSectionsEditor.vue'

const props = defineProps<{
  config: StrategyConfig
  promptSectionDefaults: PromptSectionConfig
}>()

/** LC-STD-04 §2 块 4：角色追加指令硬上限 2,000 字。 */
const MAX_ROLE_INSTRUCTION = 2000

type RoleKey = keyof RoleInstructions

const ROLES: { key: RoleKey; label: string; desc: string; placeholder: string }[] = [
  {
    key: 'analyst',
    label: '分析师',
    desc: '追加到分析师系统指令末尾，只描述关注点与判断口径，不改变固定安全块。',
    placeholder: '例如：优先观察 4h 趋势与资金费率背离；数据缺失时明确列出缺口…',
  },
  {
    key: 'trader',
    label: '交易员',
    desc: '追加到交易员系统指令末尾；交易员页同时维护原有四段提示与自定义提示。',
    placeholder: '例如：只做顺势回踩，盈亏比低于 2 不做；连续两次止损后降低仓位…',
  },
  {
    key: 'risk',
    label: '风控官',
    desc: '追加到风控官系统指令末尾，只能收紧不能放宽策略风控参数。',
    placeholder: '例如：数据不完整时不批准市价单；波动异常放大时收紧仓位上限…',
  },
  {
    key: 'executor',
    label: '执行员',
    desc: '追加到执行员系统指令末尾，最终金额与数量边界仍由系统校验。',
    placeholder: '例如：下单前复核止损方向；交易所返回未知结果时不得重试…',
  },
  {
    key: 'reviewer',
    label: '复盘员',
    desc: '追加到复盘员系统指令末尾，用于约束复盘关注点与经验表达。',
    placeholder: '例如：优先总结可复用的入场/退出规则，证据引用具体周期与订单…',
  },
]

function valueOf(key: RoleKey): string {
  return props.config.roleInstructions[key] ?? ''
}

function countOf(key: RoleKey): number {
  return valueOf(key).length
}

function isOverLimit(key: RoleKey): boolean {
  return valueOf(key).length > MAX_ROLE_INSTRUCTION
}
</script>

<template>
  <div class="role-tabs">
    <a-tabs default-active-key="analyst" size="small" class="role-tab-nav">
      <a-tab-pane v-for="role in ROLES" :key="role.key" :title="role.label">
        <div class="role-pane">
          <div class="role-head">
            <span class="role-title">{{ role.label }}追加指令</span>
            <span class="role-count" :class="{ over: isOverLimit(role.key) }">
              {{ countOf(role.key) }}/{{ MAX_ROLE_INSTRUCTION }}
            </span>
          </div>
          <div class="role-desc">{{ role.desc }}</div>
          <a-textarea
            v-model="config.roleInstructions[role.key]"
            class="mono role-text"
            :auto-size="{ minRows: 10, maxRows: 18 }"
            :error="isOverLimit(role.key)"
            :placeholder="role.placeholder"
          />
          <div v-if="isOverLimit(role.key)" class="role-error">
            超出 {{ MAX_ROLE_INSTRUCTION }} 字上限（当前 {{ countOf(role.key) }} 字），保存将被阻止。
          </div>
          <div v-else class="role-hint">
            保存时自动去除首尾空白；上限 {{ MAX_ROLE_INSTRUCTION }} 字（LC-STD-04 §2 块 4）。
          </div>

          <!-- LC-STD-04 §2：promptSections 与 customPrompt 仅交易员的策略配置摘要包含 -->
          <div v-if="role.key === 'trader'" class="trader-extras">
            <PromptSectionsEditor :config="config.promptSections" :defaults="promptSectionDefaults" />
            <div class="custom-block">
              <div class="custom-title">自定义提示</div>
              <div class="role-desc">追加到交易员系统提示词末尾的补充说明。</div>
              <a-textarea
                v-model="config.customPrompt"
                class="mono"
                placeholder="输入自定义提示词..."
                :auto-size="{ minRows: 4, maxRows: 10 }"
              />
            </div>
          </div>
        </div>
      </a-tab-pane>
    </a-tabs>
  </div>
</template>

<style scoped>
.role-tabs {
  display: flex;
  flex-direction: column;
}

.role-tab-nav :deep(.arco-tabs-nav) {
  padding: 0 2px;
}

.role-pane {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding-top: 4px;
}

.role-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.role-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.role-count {
  font-size: 11px;
  color: var(--color-text-3);
  font-variant-numeric: tabular-nums;
}

.role-count.over {
  color: rgb(var(--danger-6));
  font-weight: 600;
}

.role-desc {
  font-size: 11px;
  line-height: 1.5;
  color: var(--color-text-3);
}

.mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
}

.role-text {
  resize: vertical;
}

.role-error {
  font-size: 11px;
  line-height: 1.5;
  color: rgb(var(--danger-6));
}

.role-hint {
  font-size: 11px;
  color: var(--color-text-3);
}

.trader-extras {
  display: flex;
  flex-direction: column;
  gap: 14px;
  margin-top: 8px;
  padding-top: 12px;
  border-top: 1px solid var(--color-border-2);
}

.custom-block {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.custom-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}
</style>
