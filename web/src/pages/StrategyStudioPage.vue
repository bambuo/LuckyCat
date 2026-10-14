<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { Message, Modal } from '@arco-design/web-vue'
import { IconStar } from '@arco-design/web-vue/es/icon'
import {
  activateStrategy,
  createStrategy,
  deleteStrategy,
  duplicateStrategy,
  getStrategyDefaults,
  listStrategies,
  updateStrategy,
} from '../api'
import LuckyCatScene from '../components/trader/LuckyCatScene.vue'
import StrategyEditor from '../components/strategy/StrategyEditor.vue'
import StrategyListPanel from '../components/strategy/StrategyListPanel.vue'
import PromptPreviewPanel from '../components/strategy/PromptPreviewPanel.vue'
import StrategyTestPanel from '../components/strategy/StrategyTestPanel.vue'
import type { Strategy, StrategyConfig, StrategyDefaults } from '../types'

const strategies = ref<Strategy[]>([])
const defaults = ref<StrategyDefaults | null>(null)
const loading = ref(false)
const errorText = ref<string | null>(null)

const selectedId = ref<number | null>(null)
const saved = ref<Strategy | null>(null) // 已保存快照（列表内的权威对象）
const draft = ref<StrategyConfig | null>(null) // 编辑中的草稿（深拷贝；各编辑器就地修改）
const nameInput = ref('')
const descriptionInput = ref('')
const saving = ref(false)

// 深度比对：名称/简介/任何 config 改动（含 variant）都会置脏（修正设计稿缺陷 b）
const isDirty = computed(() => {
  if (!saved.value || !draft.value) return false
  return (
    nameInput.value !== saved.value.name ||
    (descriptionInput.value.trim() || null) !== saved.value.description ||
    JSON.stringify(draft.value) !== JSON.stringify(saved.value.config)
  )
})

function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

async function load() {
  loading.value = true
  errorText.value = null
  try {
    const [list, defaultPayload] = await Promise.all([listStrategies(), getStrategyDefaults()])
    strategies.value = list
    defaults.value = defaultPayload
    if (saved.value) {
      const refreshed = list.find((s) => s.id === saved.value?.id)
      if (refreshed) saved.value = refreshed
      else clearSelection()
    } else if (list.length > 0) {
      // 初始选中：优先激活策略，否则第一条（设计稿 §7）
      selectStrategy(list.find((s) => s.isActive) ?? list[0])
    }
  } catch (err) {
    errorText.value = err instanceof Error ? err.message : '加载失败'
    Message.error(errorText.value)
  } finally {
    loading.value = false
  }
}

// 即点即切：未保存改动直接丢弃（设计稿 §7，无确认）
function selectStrategy(strategy: Strategy) {
  selectedId.value = strategy.id
  saved.value = strategy
  draft.value = clone(strategy.config)
  nameInput.value = strategy.name
  descriptionInput.value = strategy.description ?? ''
}

function clearSelection() {
  selectedId.value = null
  saved.value = null
  draft.value = null
  nameInput.value = ''
  descriptionInput.value = ''
}

async function save() {
  if (!saved.value || !draft.value || saving.value) return
  saving.value = true
  try {
    const updated = await updateStrategy(saved.value.id, {
      name: nameInput.value.trim() || '未命名策略',
      description: descriptionInput.value.trim() || null,
      config: clone(draft.value),
    })
    saved.value = updated
    draft.value = clone(updated.config)
    nameInput.value = updated.name
    descriptionInput.value = updated.description ?? ''
    replaceInList(updated)
    Message.success('策略已保存')
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '保存失败')
  } finally {
    saving.value = false
  }
}

function replaceInList(strategy: Strategy) {
  const index = strategies.value.findIndex((s) => s.id === strategy.id)
  if (index >= 0) strategies.value[index] = strategy
}

async function createNew() {
  if (!defaults.value) return
  try {
    const created = await createStrategy({
      name: '新策略',
      description: null,
      config: clone(defaults.value.config),
    })
    strategies.value = [created, ...strategies.value]
    selectStrategy(created)
    Message.success('策略已创建')
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '创建失败')
  }
}

async function activate(strategy: Strategy) {
  try {
    await activateStrategy(strategy.id)
    await load()
    Message.success('策略已激活')
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '激活失败')
  }
}

async function duplicate(strategy: Strategy) {
  try {
    const copy = await duplicateStrategy(strategy.id)
    strategies.value = [copy, ...strategies.value]
    selectStrategy(copy)
    Message.success('已复制策略')
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '复制失败')
  }
}

function remove(strategy: Strategy) {
  Modal.confirm({
    title: '确认删除',
    content: `确定删除策略「${strategy.name}」？删除后不可恢复。`,
    okText: '删除',
    cancelText: '取消',
    maskClosable: false,
    okButtonProps: { status: 'danger' },
    onOk: async () => {
      try {
        await deleteStrategy(strategy.id)
        Message.success('策略已删除')
        if (selectedId.value === strategy.id) clearSelection()
        strategies.value = strategies.value.filter((s) => s.id !== strategy.id)
      } catch (err) {
        Message.error(err instanceof Error ? err.message : '删除失败')
        return false // Arco：onOk 返回 false 时弹窗不关闭
      }
    },
  })
}

function exportStrategy(strategy: Strategy) {
  try {
    const config = clone(strategy.config)
    // 对齐设计稿「公开分享时自动剥离」：导出即剥离外部数据源请求头（可能含密钥）
    for (const source of config.indicators.externalDataSources) source.headers = []
    const payload = {
      exported_at: new Date().toISOString(),
      version: '1.0',
      name: strategy.name,
      description: strategy.description,
      config,
    }
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `strategy_${strategy.name}_${formatDate(new Date())}.json`
    link.click()
    URL.revokeObjectURL(url)
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '导出失败')
  }
}

function formatDate(date: Date): string {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

function importStrategy() {
  const input = document.createElement('input')
  input.type = 'file'
  input.accept = '.json,application/json'
  input.onchange = async () => {
    const file = input.files?.[0]
    if (!file) return
    try {
      const parsed = JSON.parse(await file.text()) as { name?: unknown; description?: unknown; config?: unknown }
      if (
        typeof parsed?.name !== 'string' ||
        parsed.name.trim() === '' ||
        !parsed.config ||
        typeof parsed.config !== 'object'
      ) {
        Message.error('无效的策略文件')
        return
      }
      const created = await createStrategy({
        name: `${parsed.name.trim()}（导入）`,
        description:
          typeof parsed.description === 'string' && parsed.description.trim() !== ''
            ? parsed.description.trim()
            : null,
        config: normalizeImportedConfig(parsed.config as Partial<StrategyConfig>),
      })
      strategies.value = [created, ...strategies.value]
      selectStrategy(created)
      Message.success('策略已导入')
    } catch {
      Message.error('无效的策略文件')
    }
  }
  input.click()
}

// 与当前默认配置合并，保证完整形状（手工改过或缺字段的导入文件也能用）
function normalizeImportedConfig(raw: Partial<StrategyConfig>): StrategyConfig {
  const base = clone(defaults.value?.config as StrategyConfig)
  return {
    ...base,
    ...raw,
    coinSource: { ...base.coinSource, ...raw.coinSource },
    gridConfig:
      raw.gridConfig === undefined
        ? base.gridConfig
        : raw.gridConfig === null
          ? null
          : { ...(base.gridConfig as StrategyConfig['gridConfig']), ...raw.gridConfig },
    indicators: {
      ...base.indicators,
      ...raw.indicators,
      klines: { ...base.indicators.klines, ...raw.indicators?.klines },
      externalDataSources: raw.indicators?.externalDataSources ?? base.indicators.externalDataSources,
    },
    riskControl: { ...base.riskControl, ...raw.riskControl },
    promptSections: { ...base.promptSections, ...raw.promptSections },
  }
}

onMounted(load)
</script>

<template>
  <div class="studio">
    <div class="studio-header">
      <div class="header-icon"><IconStar /></div>
      <div class="header-text">
        <h1>策略工作室</h1>
        <span class="header-sub">可视化配置和测试交易策略</span>
      </div>
      <a-alert v-if="errorText" type="error" closable class="header-alert" @close="errorText = null">
        {{ errorText }}
      </a-alert>
    </div>

    <div class="studio-body">
      <aside class="studio-left">
        <StrategyListPanel
          :strategies="strategies"
          :selected-id="selectedId"
          @select="selectStrategy"
          @create="createNew"
          @import="importStrategy"
          @export="exportStrategy"
          @duplicate="duplicate"
          @remove="remove"
        />
      </aside>

      <section class="studio-main">
        <div v-if="loading" class="studio-center">
          <LuckyCatScene class="loading-cat" />
          <span class="loading-text">正在加载策略工作室…</span>
        </div>
        <div v-else-if="!draft || !defaults" class="studio-center">
          <div class="empty-icon"><IconStar /></div>
          <div class="empty-text">选择或创建策略</div>
        </div>
        <StrategyEditor
          v-else
          :key="selectedId ?? 0"
          :config="draft"
          :default-config="defaults.config"
          :prompt-section-defaults="defaults.promptSections"
          :name="nameInput"
          :description="descriptionInput"
          :is-active="saved?.isActive ?? false"
          :dirty="isDirty"
          :saving="saving"
          @update:name="nameInput = $event"
          @update:description="descriptionInput = $event"
          @save="save"
          @activate="saved && activate(saved)"
        />
      </section>

      <aside class="studio-right">
        <a-tabs v-if="draft" :key="selectedId ?? 0" class="right-tabs" :default-active-key="'preview'">
          <a-tab-pane key="preview" title="Prompt 预览">
            <PromptPreviewPanel :config="draft" />
          </a-tab-pane>
          <a-tab-pane key="test" title="AI 测试">
            <StrategyTestPanel :config="draft" :strategy-id="selectedId" />
          </a-tab-pane>
        </a-tabs>
        <div v-else class="studio-center">
          <div class="empty-text">选择策略后可预览与测试</div>
        </div>
      </aside>
    </div>
  </div>
</template>

<style scoped>
/* 三栏固定布局：页面不滚动，列内滚动（设计稿「视口高固定」；项目顶栏 56px） */
.studio {
  display: flex;
  flex-direction: column;
  height: calc(100vh - 56px);
  overflow: hidden;
  background: var(--color-bg-1);
}

.studio-header {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-shrink: 0;
  height: 56px;
  padding: 0 16px;
  background: var(--color-bg-2);
  border-bottom: 1px solid var(--color-border-2);
}

/* 品牌金块（设计稿头部条；本页唯一非 Arco 语义色的装饰元素） */
.header-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  border-radius: 8px;
  background: #f0b90b;
  color: #1d2129;
  font-size: 20px;
  flex-shrink: 0;
}

.header-text {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex: 1;
  min-width: 0;
}

.header-text h1 {
  margin: 0;
  font-size: 18px;
  font-weight: 700;
  color: var(--color-text-1);
  white-space: nowrap;
}

.header-sub {
  font-size: 12px;
  color: var(--color-text-3);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.header-alert {
  max-width: 480px;
  flex-shrink: 0;
}

.studio-body {
  display: flex;
  flex: 1;
  min-height: 0;
}

.studio-left {
  width: 192px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--color-bg-2);
  border-right: 1px solid var(--color-border-2);
}

.studio-main {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
  padding: 16px;
}

.studio-right {
  width: 420px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--color-bg-2);
  border-left: 1px solid var(--color-border-2);
}

.right-tabs {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  padding: 0 12px 12px;
}

.right-tabs :deep(.arco-tabs-content) {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.right-tabs :deep(.arco-tabs-nav) {
  padding: 0 2px;
}

.studio-center {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  height: 100%;
  color: var(--color-text-3);
}

.loading-cat {
  width: 130px;
  height: 130px;
}

.empty-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 56px;
  height: 56px;
  border: 1px solid var(--color-border-2);
  border-radius: 50%;
  font-size: 24px;
  opacity: 0.6;
}

.empty-text {
  font-size: 14px;
}
</style>
