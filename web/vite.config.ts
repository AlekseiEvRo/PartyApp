import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig({
    build: {
        outDir: '../src/PartyApp.Api/wwwroot',
        emptyOutDir: true
    },
    plugins: [
        svelte(),
        VitePWA({
            // 'prompt': новая версия ждёт, показываем баннер и обновляемся по кнопке.
            // Регистрируем SW сами (web/src/lib/pwa.ts), чтобы управлять этим процессом
            registerType: 'prompt',
            injectRegister: false,
            includeAssets: ['favicon.ico'],
            manifest: {
                name: 'PartyApp — Вечеринка',
                short_name: 'PartyApp',
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
                // Кэшируем статику для офлайн-работы
                globPatterns: ['**/*.{js,css,html,ico,png,svg,webmanifest}'],
                // Обработчик push-уведомлений добавляется в сгенерированный service worker
                importScripts: ['/push-sw.js'],
                runtimeCaching: [
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