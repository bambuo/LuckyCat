import { createRouter, createWebHistory } from 'vue-router'
import ProvidersPage from './pages/ProvidersPage.vue'
import ExchangesPage from './pages/ExchangesPage.vue'
import StrategyStudioPage from './pages/StrategyStudioPage.vue'
import TradersPage from './pages/TradersPage.vue'
import DashboardPage from './pages/DashboardPage.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/providers', name: 'providers', component: ProvidersPage },
    { path: '/exchanges', name: 'exchanges', component: ExchangesPage },
    { path: '/strategy', name: 'strategy', component: StrategyStudioPage },
    { path: '/traders', name: 'traders', component: TradersPage },
    { path: '/dashboard', name: 'dashboard', component: DashboardPage },
    // 模型提供商与加密交易所页保留（能力已收进 AI 交易员页，含验证；仅 URL 直达）
    { path: '/', redirect: '/traders' },
  ],
})

export default router
