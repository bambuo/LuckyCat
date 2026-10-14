import type { ExchangeType } from './types'

export interface ExchangePreset {
  key: ExchangeType
  label: string
  needsPassphrase: boolean
  /** 用户数据流 WS 签名需要账户 UID（目前仅 Gate.io） */
  needsUserId: boolean
  icon: string
  /** 邀请注册链接：配置后添加账户弹窗第二步的卡片右侧出现「邀请注册」入口（新标签页打开）；留空则整个入口隐藏 */
  inviteUrl?: string
}

export const EXCHANGE_PRESETS: ExchangePreset[] = [
  { key: 'Binance', label: 'Binance', needsPassphrase: false, needsUserId: false, icon: '/icons/binance.svg', inviteUrl: 'https://www.bsmkweb.cc/register?ref=1185731486' },
  { key: 'Bybit', label: 'Bybit', needsPassphrase: false, needsUserId: false, icon: '/icons/bybit.ico', inviteUrl: 'https://www.bybit.com/invite?ref=8PRZONB&medium=referral&utm_campaign=evergreen&share_to=link' },
  { key: 'OKX', label: 'OKX', needsPassphrase: true, needsUserId: false, icon: '/icons/okx.svg', inviteUrl: 'https://www.mitnpkwxvfr.com/join/M66WIHRC' },
  { key: 'Bitget', label: 'Bitget', needsPassphrase: true, needsUserId: false, icon: '/icons/bitget.ico', inviteUrl: 'https://share.glassgs.com/u/259SF9AM?clacCode=D9PR4GVV' },
  { key: 'GateIo', label: 'Gate.io', needsPassphrase: false, needsUserId: true, icon: '/icons/gateio.ico', inviteUrl: 'https://www.gate.com/zh/referral/earn-together/invite/VFZHBGPEAQ?ref=VFZHBGPEAQ&ref_type=103&utm_cmp=rXJBDjtJ&activity_id=1790170412453' },
  { key: 'Kucoin', label: 'KuCoin', needsPassphrase: true, needsUserId: false, icon: '/icons/kucoin.svg', inviteUrl: 'https://www.kucoin.com/r/rf/CXEJ81WK' },
]

export function getExchangePreset(key: ExchangeType): ExchangePreset | undefined {
  return EXCHANGE_PRESETS.find((preset) => preset.key === key)
}

export function exchangeLabel(key: ExchangeType): string {
  return getExchangePreset(key)?.label ?? key
}

export function exchangeLetter(key: ExchangeType): string {
  return key.charAt(0).toUpperCase()
}
