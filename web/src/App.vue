<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import CatTransition from './components/trader/CatTransition.vue'

const route = useRoute()
const router = useRouter()

const activeKey = computed(() => route.path)

function onMenuClick(key: string) {
  if (key !== route.path) router.push(key)
}
</script>

<template>
  <a-layout class="app-layout">
    <a-layout-header class="app-header">
      <div class="app-brand">
        <img class="brand-icon" src="/logo.svg" alt="" />
        <span>招财猫</span>
      </div>
      <a-menu
        mode="horizontal"
        :selected-keys="[activeKey]"
        class="app-nav"
        @menu-item-click="onMenuClick"
      >
        <a-menu-item key="/traders">AI 交易员</a-menu-item>
        <a-menu-item key="/strategy">策略工作室</a-menu-item>
      </a-menu>
    </a-layout-header>
    <a-layout-content>
      <router-view />
    </a-layout-content>
    <CatTransition />
  </a-layout>
</template>

<style scoped>
.app-layout {
  min-height: 100vh;
}

.app-header {
  display: flex;
  align-items: center;
  gap: 32px;
  height: 56px;
  padding: 0 24px;
  background: var(--color-bg-2);
  border-bottom: 1px solid var(--color-border-2);
}

.app-brand {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 16px;
  font-weight: 600;
  color: var(--color-text-1);
  flex-shrink: 0;
}

.brand-icon {
  width: 28px;
  height: 28px;
  flex-shrink: 0;
  display: block;
}

.app-nav {
  flex: 1;
  background: transparent;
}
</style>
