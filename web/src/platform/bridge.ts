import { Capacitor } from '@capacitor/core'

/**
 * 세 채널(PC 웹 · Windows 앱 · 모바일 앱)의 차이를 한곳에서 흡수한다.
 * 화면 코드는 platform.kind와 아래 메서드만 쓰고, 채널별 분기를 흩뿌리지 않는다.
 */
export type PlatformKind = 'web' | 'desktop' | 'mobile'

interface DesktopBridge {
  isDesktop: true
  invoke<T = unknown>(cmd: string, args?: Record<string, unknown>): Promise<T>
}

declare global {
  interface Window {
    dyoneDesktop?: DesktopBridge
  }
}

export interface Platform {
  kind: PlatformKind
  /** 운영체제 알림. Windows 앱은 토스트(누르면 link 화면으로 이동), 모바일은 (FCM 연결 후) 푸시, PC 웹은 화면 안 알림만 쓴다. */
  showSystemNotification(title: string, body: string, link?: string): Promise<void>
  openExternal(url: string): Promise<void>
  /** Windows 앱의 Local Broker 명령. 다른 채널에서는 지원하지 않는다. */
  broker<T = unknown>(cmd: string, args?: Record<string, unknown>): Promise<T>
  storage: { get(key: string): string | null; set(key: string, value: string): void; remove(key: string): void }
}

const webStorage = {
  get: (k: string) => {
    try {
      return localStorage.getItem(k)
    } catch {
      return null
    }
  },
  set: (k: string, v: string) => {
    try {
      localStorage.setItem(k, v)
    } catch {
      /* 저장소 차단 환경 */
    }
  },
  remove: (k: string) => {
    try {
      localStorage.removeItem(k)
    } catch {
      /* 저장소 차단 환경 */
    }
  },
}

function detect(): PlatformKind {
  if (window.dyoneDesktop?.isDesktop) return 'desktop'
  if (Capacitor.isNativePlatform()) return 'mobile'
  return 'web'
}

function createPlatform(): Platform {
  const kind = detect()
  const desktop = window.dyoneDesktop

  return {
    kind,
    async showSystemNotification(title, body, link) {
      if (kind === 'desktop' && desktop) await desktop.invoke('notify.toast', link ? { title, body, link } : { title, body })
      // mobile: FCM 연결 단계에서 구현 · web: 화면 안 알림만 사용(Web Push 미사용)
    },
    async openExternal(url) {
      if (kind === 'desktop' && desktop) await desktop.invoke('shell.openExternal', { url })
      else window.open(url, '_blank', 'noopener')
    },
    async broker(cmd, args) {
      if (kind !== 'desktop' || !desktop) throw new Error('설치형 앱(DY ONE Desktop)에서만 사용할 수 있는 기능입니다.')
      return desktop.invoke(cmd, args)
    },
    // 토큰 보관: 지금은 모든 채널에서 localStorage. Windows 앱은 추후 DPAPI(브리지)로 옮긴다.
    storage: webStorage,
  }
}

export const platform = createPlatform()
