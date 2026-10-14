<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Message, Modal } from '@arco-design/web-vue'
import {
  IconCode,
  IconPlus,
  IconRobot,
  IconStorage,
  IconUser,
} from '@arco-design/web-vue/es/icon'
import {
  deleteExchange,
  deleteProvider,
  deleteTrader,
  listExchanges,
  listProviders,
  listStrategies,
  listTraders,
  startTrader,
  stopTrader,
} from '../api'
import { playCatTransition } from '../catTransition'
import LuckyCatScene from '../components/trader/LuckyCatScene.vue'
import { exchangeLabel, getExchangePreset } from '../exchanges'
import { letterOf, matchPreset } from '../presets'
import type { ExchangeAccount, Provider, Strategy, Trader } from '../types'
import ProviderForm from '../components/ProviderForm.vue'
import ExchangeForm from '../components/ExchangeForm.vue'
import VerifyPanel from '../components/VerifyPanel.vue'
import ExchangeVerifyPanel from '../components/ExchangeVerifyPanel.vue'
import TraderForm from '../components/trader/TraderForm.vue'

const router = useRouter()

const traders = ref<Trader[]>([])
const providers = ref<Provider[]>([])
const accounts = ref<ExchangeAccount[]>([])
const strategies = ref<Strategy[]>([])
const tradersLoading = ref(false)

const traderFormVisible = ref(false)
const editingTrader = ref<Trader | null>(null)
const providerFormVisible = ref(false)
const editingProvider = ref<Provider | null>(null)
const exchangeFormVisible = ref(false)
const editingAccount = ref<ExchangeAccount | null>(null)

// 验证面板（提供商连通性四检 / 交易所凭证与行情二检）——能力已收进本页
const verifyProviderVisible = ref(false)
const verifyingProvider = ref<Provider | null>(null)
const verifyAccountVisible = ref(false)
const verifyingAccount = ref<ExchangeAccount | null>(null)

function openVerifyProvider(provider: Provider) {
  verifyingProvider.value = provider
  verifyProviderVisible.value = true
}

function openVerifyAccount(account: ExchangeAccount) {
  verifyingAccount.value = account
  verifyAccountVisible.value = true
}

const canCreateTrader = computed(() => providers.value.length > 0 && accounts.value.length > 0)
const missingHint = computed(() => {
  const noModel = providers.value.length === 0
  const noExchange = accounts.value.length === 0
  if (noModel && noExchange) return '尚未配置 AI 模型与交易所，请先完成两项配置'
  if (noModel) return '尚未配置 AI 模型，请先添加模型'
  if (noExchange) return '尚未配置交易所，请先添加交易所'
  return null
})

interface UsageStat {
  total: number
  running: number
  usedByRunning: boolean
}

function usageOf(predicate: (t: Trader) => boolean): UsageStat {
  const list = traders.value.filter(predicate)
  return {
    total: list.length,
    running: list.filter((t) => t.isRunning).length,
    usedByRunning: list.some((t) => t.isRunning),
  }
}

const providerUsage = computed(() => {
  const map = new Map<number, UsageStat>()
  for (const p of providers.value) map.set(p.id, usageOf((t) => t.providerId === p.id))
  return map
})

const accountUsage = computed(() => {
  const map = new Map<number, UsageStat>()
  for (const a of accounts.value) map.set(a.id, usageOf((t) => t.exchangeAccountId === a.id))
  return map
})

async function loadConfig() {
  try {
    const [p, a, s] = await Promise.all([listProviders(), listExchanges(), listStrategies()])
    providers.value = p
    accounts.value = a
    strategies.value = s
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '加载配置失败')
  }
}

async function loadTraders(silent = false) {
  if (!silent) tradersLoading.value = true
  try {
    traders.value = await listTraders()
  } catch (err) {
    if (!silent) Message.error(err instanceof Error ? err.message : '加载交易员失败')
  } finally {
    if (!silent) tradersLoading.value = false
  }
}

let timer: number | undefined

onMounted(async () => {
  await Promise.all([loadConfig(), loadTraders()])
  timer = window.setInterval(() => void loadTraders(true), 5000)
})

onUnmounted(() => {
  if (timer !== undefined) window.clearInterval(timer)
})

function openCreateTrader() {
  if (!canCreateTrader.value) return
  editingTrader.value = null
  traderFormVisible.value = true
}

function openCreateProvider() {
  editingProvider.value = null
  providerFormVisible.value = true
}

function openCreateAccount() {
  editingAccount.value = null
  exchangeFormVisible.value = true
}

function openEditTrader(trader: Trader) {
  editingTrader.value = trader
  traderFormVisible.value = true
}

function openEditProvider(provider: Provider) {
  editingProvider.value = provider
  providerFormVisible.value = true
}

function openEditAccount(account: ExchangeAccount) {
  editingAccount.value = account
  exchangeFormVisible.value = true
}

function start(trader: Trader) {
  const isLive = trader.executionMode === 'live'
  Modal.confirm({
    title: isLive ? '以实盘模式启动？' : '以演练模式启动？',
    content: isLive
      ? `「${trader.name}」将以真实资金按扫描间隔自动下单（每 ${trader.scanIntervalMinutes} 分钟）。启动前会校验交易所下单能力，无法附加止损时拒绝自动开仓。`
      : `「${trader.name}」将按扫描间隔运行 AI 决策与审计留痕（每 ${trader.scanIntervalMinutes} 分钟），不下任何真实订单。`,
    okText: '启动',
    okButtonProps: { status: isLive ? 'danger' : 'normal' },
    onOk: async () => {
      try {
        await startTrader(trader.id)
        Message.success('已启动')
        await loadTraders()
      } catch (err) {
        Message.error(err instanceof Error ? err.message : '启动失败')
      }
    },
  })
}

async function stop(trader: Trader) {
  try {
    await stopTrader(trader.id)
    Message.success('已停止')
    await loadTraders()
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '停止失败')
  }
}

async function remove(trader: Trader) {
  try {
    await deleteTrader(trader.id)
    Message.success('已删除')
    await loadTraders()
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '删除失败')
  }
}

function viewDashboard(trader: Trader) {
  playCatTransition(trader.name)
  void router.push({ path: '/dashboard', query: { trader: String(trader.id) } })
}

async function removeProvider(provider: Provider) {
  try {
    await deleteProvider(provider.id)
    Message.success('已删除')
    providerFormVisible.value = false
    await loadConfig()
    await loadTraders(true)
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '删除失败')
  }
}

async function removeAccount(account: ExchangeAccount) {
  try {
    await deleteExchange(account.id)
    Message.success('已删除')
    exchangeFormVisible.value = false
    await loadConfig()
    await loadTraders(true)
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '删除失败')
  }
}

// 行图标：与提供商/交易所列表页同款（预设图标优先，回退首字母/字母）
function providerIcon(provider: Provider): string {
  return matchPreset(provider)?.icon ?? ''
}

function providerLetter(provider: Provider): string {
  const preset = matchPreset(provider)
  return preset ? (preset.letter ?? letterOf(preset.label)) : letterOf(provider.name)
}

function accountIcon(account: ExchangeAccount): string {
  return getExchangePreset(account.exchangeType)?.icon ?? ''
}

function modelColor(providerName: string): string {
  return providerName.toLowerCase().includes('deepseek') ? '#f0b90b' : '#c084fc'
}
</script>

<template>
  <div class="page-content">
    <div class="page-header">
      <div class="header-icon"><IconRobot /></div>
      <div class="header-text">
        <h1>AI 交易员</h1>
        <a-tag color="gold" size="small">{{ traders.length }} 个交易员</a-tag>
      </div>
      <div class="header-actions">
        <a-button @click="openCreateProvider">
          <template #icon><IconPlus /></template>
          添加模型
        </a-button>
        <a-button @click="openCreateAccount">
          <template #icon><IconPlus /></template>
          添加交易所
        </a-button>
        <a-tooltip :content="canCreateTrader ? '' : missingHint ?? ''" :disabled="canCreateTrader">
          <a-button type="primary" :disabled="!canCreateTrader" @click="openCreateTrader">
            <template #icon><IconPlus /></template>
            创建交易员
          </a-button>
        </a-tooltip>
      </div>
    </div>

    <div class="config-grid">
      <!-- AI 模型卡片 -->
      <a-card class="config-card">
        <template #title>
          <a-space :size="8"><IconCode :style="{ color: 'rgb(var(--primary-6))' }" />AI 模型</a-space>
        </template>
        <template #extra>
          <span class="card-extra">{{ providers.length }} 个已配置</span>
        </template>

        <div v-if="providers.length === 0" class="mini-empty">
          <div class="mini-empty-text">暂无已配置的模型</div>
          <a-button size="small" @click="openCreateProvider">
            <template #icon><IconPlus /></template>
            添加第一个模型
          </a-button>
        </div>
        <div v-else class="row-list">
          <div
            v-for="p in providers"
            :key="p.id"
            class="config-row"
            :style="{ opacity: providerUsage.get(p.id)?.usedByRunning ? 0.7 : 1 }"
            @click="openEditProvider(p)"
          >
            <div class="row-icon">
              <img v-if="providerIcon(p)" :src="providerIcon(p)" :alt="p.name" />
              <span v-else>{{ providerLetter(p) }}</span>
            </div>
            <div class="row-main">
              <div class="row-name">{{ p.name }}</div>
              <div class="row-sub">{{ p.model }}</div>
            </div>
            <a-tag v-if="(providerUsage.get(p.id)?.total ?? 0) > 0" :color="(providerUsage.get(p.id)?.running ?? 0) > 0 ? 'green' : 'gold'" size="small">
              {{ providerUsage.get(p.id)?.running }}/{{ providerUsage.get(p.id)?.total }} 活跃
            </a-tag>
            <a-button size="mini" type="outline" status="success" @click.stop="openVerifyProvider(p)">验证</a-button>
          </div>
        </div>
      </a-card>

      <!-- 交易所卡片 -->
      <a-card class="config-card">
        <template #title>
          <a-space :size="8"><IconStorage :style="{ color: 'rgb(var(--primary-6))' }" />交易所</a-space>
        </template>
        <template #extra>
          <span class="card-extra">{{ accounts.length }} 个已配置</span>
        </template>

        <div v-if="accounts.length === 0" class="mini-empty">
          <div class="mini-empty-text">暂无已配置的交易所</div>
          <a-button size="small" @click="openCreateAccount">
            <template #icon><IconPlus /></template>
            添加第一个交易所
          </a-button>
        </div>
        <div v-else class="row-list">
          <div
            v-for="a in accounts"
            :key="a.id"
            class="config-row"
            :style="{ opacity: accountUsage.get(a.id)?.usedByRunning ? 0.7 : 1 }"
            @click="openEditAccount(a)"
          >
            <div class="row-icon">
              <img v-if="accountIcon(a)" :src="accountIcon(a)" :alt="a.exchangeType" />
              <span v-else>{{ a.exchangeType.slice(0, 1).toUpperCase() }}</span>
            </div>
            <div class="row-main">
              <div class="row-name">{{ exchangeLabel(a.exchangeType).toUpperCase() }}</div>
            </div>
            <a-tag color="gray" size="small">{{ a.accountName || 'DEFAULT' }}</a-tag>
            <a-tag v-if="(accountUsage.get(a.id)?.total ?? 0) > 0" :color="(accountUsage.get(a.id)?.running ?? 0) > 0 ? 'green' : 'gold'" size="small">
              {{ accountUsage.get(a.id)?.running }}/{{ accountUsage.get(a.id)?.total }} 活跃
            </a-tag>
            <a-button size="mini" type="outline" status="success" @click.stop="openVerifyAccount(a)">验证</a-button>
          </div>
        </div>
      </a-card>
    </div>

    <!-- 当前交易员 -->
    <a-card class="traders-card">
      <template #title>
        <a-space :size="8"><IconUser :style="{ color: 'rgb(var(--primary-6))' }" />当前交易员</a-space>
      </template>
      <template #extra>
        <a-tag v-if="traders.length > 0" color="gold" size="small">{{ traders.length }} 个交易员</a-tag>
      </template>

      <div v-if="tradersLoading" class="traders-loading">
        <LuckyCatScene class="loading-cat" />
        <span class="loading-text">正在加载交易员…</span>
      </div>

      <div v-else-if="traders.length === 0" class="traders-empty">
        <div class="empty-icon"><IconRobot /></div>
        <div class="empty-title">暂无交易员</div>
        <div class="empty-sub">创建您的第一个交易员</div>
        <a-alert v-if="missingHint" type="warning" class="empty-alert">{{ missingHint }}</a-alert>
        <a-tooltip :content="canCreateTrader ? '' : missingHint ?? ''" :disabled="canCreateTrader">
          <a-button type="primary" :disabled="!canCreateTrader" @click="openCreateTrader">
            <template #icon><IconPlus /></template>
            创建交易员
          </a-button>
        </a-tooltip>
      </div>

      <div v-else class="trader-list">
        <div v-for="trader in traders" :key="trader.id" class="trader-row">
          <div class="row-icon robot"><IconRobot /></div>
          <div class="trader-main">
            <div class="trader-name">{{ trader.name }}</div>
            <div class="trader-sub">
              <span class="model-name" :style="{ color: modelColor(trader.providerName) }">{{ trader.providerName }} · {{ trader.model }}</span>
              <span class="dot">•</span>
              <span>{{ exchangeLabel(trader.exchangeType).toUpperCase() }} - {{ trader.accountName }}</span>
              <span class="dot">•</span>
              <span>{{ trader.strategyName }}</span>
              <a-tag :color="trader.executionMode === 'live' ? 'red' : 'arcoblue'" size="small" class="mode-tag">
                {{ trader.executionMode === 'live' ? '实盘' : '演练' }}
              </a-tag>
            </div>
          </div>
          <a-tag :color="trader.isRunning ? 'green' : 'red'" size="small">
            {{ trader.isRunning ? '运行中' : '已停止' }}
          </a-tag>
          <div class="trader-actions">
            <a-button size="small" type="text" @click="viewDashboard(trader)">查看</a-button>
            <a-button size="small" type="outline" status="warning" :disabled="trader.isRunning" @click="openEditTrader(trader)">
              编辑
            </a-button>
            <a-button
              v-if="!trader.isRunning"
              size="small"
              type="outline"
              status="success"
              @click="start(trader)"
            >
              启动
            </a-button>
            <a-button v-else size="small" type="outline" status="danger" @click="stop(trader)">停止</a-button>
            <a-popconfirm content="确定要删除此交易员吗？" @ok="remove(trader)">
              <a-button size="small" type="outline" status="danger">删除</a-button>
            </a-popconfirm>
          </div>
        </div>
      </div>
    </a-card>

    <!-- 弹窗 -->
    <TraderForm
      v-model:visible="traderFormVisible"
      :trader="editingTrader"
      :providers="providers"
      :accounts="accounts"
      :strategies="strategies"
      @saved="loadTraders"
    />
    <ProviderForm
      v-model:visible="providerFormVisible"
      :provider="editingProvider"
      :providers="providers"
      @saved="loadConfig"
      @remove="editingProvider && removeProvider(editingProvider)"
    />
    <ExchangeForm
      v-model:visible="exchangeFormVisible"
      :account="editingAccount"
      :accounts="accounts"
      @saved="loadConfig"
      @remove="editingAccount && removeAccount(editingAccount)"
    />
    <VerifyPanel v-model:visible="verifyProviderVisible" :provider="verifyingProvider" />
    <ExchangeVerifyPanel v-model:visible="verifyAccountVisible" :account="verifyingAccount" />
  </div>
</template>

<style scoped>
.page-content {
  width: 100%;
  max-width: 1200px;
  margin: 0 auto;
  padding: 24px;
}

.page-header {
  display: flex;
  align-items: center;
  gap: 16px;
  padding-bottom: 20px;
  margin-bottom: 24px;
  border-bottom: 1px solid var(--color-border-2);
}

.header-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 52px;
  height: 52px;
  border: 1px solid rgba(240, 185, 11, 0.3);
  border-radius: 12px;
  background: var(--color-bg-2);
  color: rgb(var(--primary-6));
  font-size: 26px;
}

.header-text {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: 1;
  min-width: 0;
}

.header-text h1 {
  margin: 0;
  font-size: 24px;
  font-weight: 600;
  color: var(--color-text-1);
}

.header-actions {
  display: flex;
  gap: 8px;
}

.config-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
  margin-bottom: 24px;
}

@media (max-width: 900px) {
  .config-grid {
    grid-template-columns: 1fr;
  }
}

.card-extra {
  font-size: 12px;
  color: var(--color-text-3);
}

.row-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.config-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 12px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
  cursor: pointer;
  transition: background-color 0.15s;
}

.config-row:hover {
  background: var(--color-fill-1);
}

.row-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border-radius: 50%;
  background: var(--color-fill-2);
  color: rgb(var(--primary-6));
  font-size: 14px;
  font-weight: 700;
  flex-shrink: 0;
}

.row-icon img {
  width: 22px;
  height: 22px;
  border-radius: 4px;
}

.row-icon.robot {
  font-size: 18px;
}

.row-main {
  flex: 1;
  min-width: 0;
}

.row-name {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.row-sub {
  font-size: 12px;
  color: var(--color-text-3);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}

.mini-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 28px 0;
}

.mini-empty-text {
  font-size: 13px;
  color: var(--color-text-3);
}

.traders-loading {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 48px 0;
}

.loading-cat {
  width: 120px;
  height: 120px;
}

.loading-text {
  color: var(--color-text-3);
  font-size: 13px;
}

.traders-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 48px 20px;
}

.empty-icon {
  font-size: 48px;
  color: var(--color-text-4);
}

.empty-title {
  font-size: 18px;
  color: var(--color-text-3);
}

.empty-sub {
  font-size: 13px;
  color: var(--color-text-3);
}

.empty-alert {
  max-width: 420px;
}

.trader-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.trader-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 16px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-bg-1);
  flex-wrap: wrap;
}

.trader-main {
  flex: 1;
  min-width: 0;
}

.trader-name {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.trader-sub {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--color-text-3);
  flex-wrap: wrap;
}

.model-name {
  font-weight: 600;
}

.dot {
  color: var(--color-text-4);
}

.mode-tag {
  margin-left: 2px;
}

.trader-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}
</style>
