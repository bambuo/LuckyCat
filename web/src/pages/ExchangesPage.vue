<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconCopy, IconPlus, IconSwap } from '@arco-design/web-vue/es/icon'
import { deleteExchange, getOutboundIp, listExchanges } from '../api'
import ExchangeForm from '../components/ExchangeForm.vue'
import ExchangeList from '../components/ExchangeList.vue'
import ExchangeVerifyPanel from '../components/ExchangeVerifyPanel.vue'
import type { ExchangeAccount } from '../types'

const accounts = ref<ExchangeAccount[]>([])
const loading = ref(false)
const outboundIp = ref<string | null>(null)
const formVisible = ref(false)
const editingAccount = ref<ExchangeAccount | null>(null)
const verifyVisible = ref(false)
const verifyingAccount = ref<ExchangeAccount | null>(null)

async function load() {
  loading.value = true
  try {
    accounts.value = await listExchanges()
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '加载失败')
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingAccount.value = null
  formVisible.value = true
}

function openEdit(account: ExchangeAccount) {
  editingAccount.value = account
  formVisible.value = true
}

function openVerify(account: ExchangeAccount) {
  verifyingAccount.value = account
  verifyVisible.value = true
}

async function remove(account: ExchangeAccount): Promise<boolean> {
  try {
    await deleteExchange(account.id)
    Message.success('已删除')
    await load()
    return true
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '删除失败')
    return false
  }
}

// 弹窗内删除：成功后关闭弹窗
async function removeFromForm() {
  const target = editingAccount.value
  if (target && (await remove(target))) formVisible.value = false
}

async function loadOutboundIp() {
  try {
    outboundIp.value = (await getOutboundIp()).ip
  } catch {
    // 查询失败时静默隐藏
    outboundIp.value = null
  }
}

async function copyOutboundIp() {
  if (!outboundIp.value) return
  try {
    await navigator.clipboard.writeText(outboundIp.value)
    Message.success('已复制出口 IP')
  } catch {
    Message.error('复制失败，请手动选择复制')
  }
}

onMounted(() => {
  void load()
  void loadOutboundIp()
})
</script>

<template>
  <div class="page-content">
    <div class="page-header">
      <div class="header-icon"><IconSwap /></div>
      <div class="header-text">
        <h1>加密交易所</h1>
        <a-tag color="gray" size="small">{{ accounts.length }} 个账户</a-tag>
        <span v-if="outboundIp" class="outbound-ip">
          <span class="outbound-label">出口 IP</span>
          <span class="outbound-value">{{ outboundIp }}</span>
          <span
            class="outbound-copy"
            role="button"
            tabindex="0"
            title="复制出口 IP"
            aria-label="复制出口 IP"
            @click="copyOutboundIp"
            @keydown.enter.prevent="copyOutboundIp"
          >
            <IconCopy />
          </span>
        </span>
      </div>
      <a-button type="primary" @click="openCreate">
        <template #icon><IconPlus /></template>
        添加交易所
      </a-button>
    </div>

    <a-card>
      <template #title>
        <a-space :size="8">
          <IconSwap :style="{ color: 'rgb(var(--primary-6))' }" />
          已配置交易所
        </a-space>
      </template>
      <template #extra>
        <span class="configured-count">{{ accounts.length }} 个已配置</span>
      </template>
      <ExchangeList
        :accounts="accounts"
        :loading="loading"
        @create="openCreate"
        @verify="openVerify"
        @edit="openEdit"
        @remove="remove"
      />
    </a-card>

    <ExchangeForm
      v-model:visible="formVisible"
      :account="editingAccount"
      :accounts="accounts"
      @saved="load"
      @remove="removeFromForm"
    />
    <ExchangeVerifyPanel v-model:visible="verifyVisible" :account="verifyingAccount" />
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
  border: 1px solid var(--color-border-2);
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

.configured-count {
  font-size: 12px;
  color: var(--color-text-3);
}

.outbound-ip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 1px 4px 1px 10px;
  border: 1px solid var(--color-border-2);
  border-radius: 999px;
  background: var(--color-fill-1);
  font-size: 12px;
  line-height: 20px;
  color: var(--color-text-3);
  white-space: nowrap;
}

.outbound-value {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  color: var(--color-text-2);
}

.outbound-copy {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  cursor: pointer;
  color: var(--color-text-3);
  transition: background-color 0.15s, color 0.15s;
}

.outbound-copy:hover {
  background: var(--color-fill-2);
  color: rgb(var(--primary-6));
}
</style>
