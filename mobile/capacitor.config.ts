import type { CapacitorConfig } from '@capacitor/cli'

/**
 * 웹 로딩 방식: 설계안대로 운영에서는 서버 URL을 띄우는 방식을 권장한다(서버 배포 = 세 채널 동시 반영).
 * - 개발: DYONE_APP_URL을 지정하면 그 주소(예: PC의 Vite 개발 서버)를 띄운다.
 * - 지정하지 않으면 ../web/dist 빌드 결과를 앱 안에 넣어 띄운다(이 경우 web 빌드 시 VITE_API_BASE 필요).
 */
const appUrl = process.env.DYONE_APP_URL

const config: CapacitorConfig = {
  appId: 'com.doolimyaskawa.dyone',
  appName: 'DY ONE',
  webDir: '../web/dist',
  ...(appUrl ? { server: { url: appUrl, cleartext: appUrl.startsWith('http://') } } : {}),
}

export default config
