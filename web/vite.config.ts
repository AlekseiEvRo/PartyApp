import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig({
    define: {
        // Штамп сборки: видно в админке, чтобы понимать, какая версия загружена
        __APP_BUILD__: JSON.stringify(new Date().toISOString())
    },
    build: {
        outDir: '../src/PartyApp.Api/wwwroot',
        emptyOutDir: true
    },
    plugins: [
        svelte(),
        VitePWA({
            // Новый SW вступает в силу сам (skipWaiting) — обновления приходят
            // без клика, старые клиенты перезагружаются автоматически
            registerType: 'autoUpdate',
            // Регистрируем SW сами (web/src/lib/pwa.ts): проверка обновлений + страховка
            injectRegister: false,
            includeAssets: ['favicon.ico'],
            manifest: {
                name: 'PartyApp — Вечеринка',
                short_name: 'PartyApp',
                // Стабильный идентификатор приложения (важно для WebAPK на Android)
                id: '/',
                description: 'Интерактивное приложение для вечеринки с друзьями',
                lang: 'ru',
                theme_color: '#0f0f23',
                background_color: '#0f0f23',
                display: 'standalone',
                orientation: 'portrait',
                scope: '/',
                start_url: '/',
                icons: [
                    {
                        src: 'icon-192.png',
                        sizes: '192x192',
                        type: 'image/png'
                    },
                    {
                        src: 'icon-512.png',
                        sizes: '512x512',
                        type: 'image/png'
                    },
                    {
                        src: 'icon-512-maskable.png',
                        sizes: '512x512',
                        type: 'image/png',
                        purpose: 'maskable'
                    }
                ]
            },
            workbox: {
                // HTML НЕ прекешируем: навигации уходят в сеть и всегда отдают
                // свежую оболочку, даже если service worker ещё не обновился.
                // Ассеты с хэшами в имени прекешируются для скорости.
                globPatterns: ['**/*.{js,css,ico,png,svg,webmanifest}'],
                navigateFallback: null,
                cleanupOutdatedCaches: true,
                // Обработчик push-уведомлений добавляется в сгенерированный service worker
                importScripts: ['/push-sw.js'],
                runtimeCaching: [
                    {
                        // Страницы: сеть в приоритете, кэш — только офлайн
                        urlPattern: ({ request }) => request.mode === 'navigate',
                        handler: 'NetworkFirst',
                        options: {
                            cacheName: 'pages',
                            networkTimeoutSeconds: 5,
                            expiration: {
                                maxEntries: 10,
                                maxAgeSeconds: 60 * 60 * 24
                            }
                        }
                    },
                    {
                        // API запросы — сначала сеть, потом кэш
                        urlPattern: /^\/api\/.*/i,
                        handler: 'NetworkFirst',
                        options: {
                            cacheName: 'api-cache',
                            expiration: {
                                maxEntries: 50,
                                maxAgeSeconds: 60 * 60 // 1 час
                            }
                        }
                    }
                ]
            },
            // Для разработки можно включить SW в dev-режиме (не рекомендуется)
            devOptions: {
                enabled: false
            }
        })
    ],
    server: {
        allowedHosts: ['party-app.online'],
        host: true,
        port: 5173,
        proxy: {
            '/api': {
                target: 'http://localhost:5000',
                changeOrigin: true
            },
            '/hubs': {
                target: 'http://localhost:5000',
                changeOrigin: true,
                ws: true
            }
        }
    }
});