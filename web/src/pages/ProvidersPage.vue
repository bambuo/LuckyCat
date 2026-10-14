<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconCode, IconPlus, IconRobot } from '@arco-design/web-vue/es/icon'
import { deleteProvider, listProviders } from '../api'
import type { Provider } from '../types'
import ProviderForm from '../components/ProviderForm.vue'
import ProviderList from '../components/ProviderList.vue'
import VerifyPanel from '../components/VerifyPanel.vue'

const providers = ref<Provider[]>([])
const loading = ref(false)
const formVisible = ref(false)
const editingProvider = ref<Provider | null>(null)
const verifyVisible = ref(false)
const verifyingProvider = ref<Provider | null>(null)

async function load() {
  loading.value = true
  try {
    providers.value = await listProviders()
  } catch (err) {
    Message.error(err instanceof Error ? err.message : '加载失败')
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingProvider.value = null
  formVisible.value = true
}

function openEdit(provider: Provider) {
  editingProvider.value = provider
  formVisible.value = true
}

function openVerify(provider: Provider) {
  verifyingProvider.value = provider
  verifyVisible.value = true
}

async function remove(provider: Provider): Promise<boolean> {
  try {
    await deleteProvider(provider.id)
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
  const target = editingProvider.value
  if (target && (await remove(target))) formVisible.value = false
}

onMounted(load)
</script>

<template>
  <div class="page-content">
    <div class="page-header">
      <div class="header-icon"><IconRobot /></div>
      <div class="header-text">
        <h1>模型提供商验证</h1>
        <a-tag color="gray" size="small">{{ providers.length }} 个提供商</a-tag>
      </div>
      <a-button type="primary" @click="openCreate">
        <template #icon><IconPlus /></template>
        添加提供商
      </a-button>
    </div>

    <a-card>
      <template #title>
        <a-space :size="8">
          <IconCode :style="{ color: 'rgb(var(--primary-6))' }" />
          模型提供商
        </a-space>
      </template>
      <template #extra>
        <span class="configured-count">{{ providers.length }} 个已配置</span>
      </template>
      <ProviderList
        :providers="providers"
        :loading="loading"
        @create="openCreate"
        @edit="openEdit"
        @remove="remove"
        @verify="openVerify"
      />
    </a-card>

    <ProviderForm
      v-model:visible="formVisible"
      :provider="editingProvider"
      :providers="providers"
      @saved="load"
      @remove="removeFromForm"
    />
    <VerifyPanel v-model:visible="verifyVisible" :provider="verifyingProvider" />
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
</style>
