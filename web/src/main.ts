import { createApp } from 'vue'
import ArcoVue from '@arco-design/web-vue'
import '@arco-design/web-vue/dist/arco.css'
import './style.css'
import App from './App.vue'
import router from './router'
import { installDesktopNavigation, isDesktopMode } from './desktop'

if (isDesktopMode()) {
  installDesktopNavigation((path) => router.push(path))
}

createApp(App).use(ArcoVue).use(router).mount('#app')
