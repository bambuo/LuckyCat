<script setup lang="ts">
import { useCatTransition } from '../../catTransition'
import LuckyCatScene from './LuckyCatScene.vue'

const cat = useCatTransition()
</script>

<template>
  <Teleport to="body">
    <div v-if="cat.active" class="cat-overlay" :class="{ leaving: cat.leaving }">
      <LuckyCatScene class="cat-svg" />
      <div class="cat-pill">招财进宝</div>
      <div class="cat-caption">正在前往「{{ cat.label }}」看板…</div>
    </div>
  </Teleport>
</template>

<style scoped>
.cat-overlay {
  position: fixed;
  inset: 0;
  z-index: 3000;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  background:
    radial-gradient(120% 120% at 50% 40%, rgba(245, 197, 66, 0.22), rgba(255, 255, 255, 0) 58%),
    rgba(255, 251, 243, 0.93);
  backdrop-filter: blur(12px);
  animation: overlay-in 0.18s ease both;
}

.cat-overlay.leaving {
  opacity: 0;
  transition: opacity 0.38s ease;
  pointer-events: none;
}

.cat-svg {
  width: 176px;
  height: 176px;
}

.cat-pill {
  padding: 2px 12px;
  border: 1px solid rgba(216, 158, 31, 0.45);
  border-radius: 999px;
  background: rgba(245, 197, 66, 0.14);
  color: #a97708;
  font-size: 12px;
  letter-spacing: 3px;
  text-indent: 3px;
}

.cat-caption {
  color: var(--color-text-1);
  font-size: 15px;
  font-weight: 600;
  letter-spacing: 0.5px;
}

@keyframes overlay-in {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}
</style>
