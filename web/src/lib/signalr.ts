import * as signalR from '@microsoft/signalr';
import {
    activeEvents,
    connectionState,
    showToast,
    balance,
    spyGameRole,
    photosVersion,
    wishesVersion,
    photoRemovedId,
    wishRemovedId,
    moderationVersion
} from './stores';
import { getToken } from './api';
import { showBrowserNotification, isNotificationSupported } from './notifications';

let connection: signalR.HubConnection | null = null;

export function getConnection() {
    return connection;
}

export async function connect(): Promise<void> {
    if (connection && connection.state === signalR.HubConnectionState.Connected) {
        return;
    }

    connectionState.set('connecting');

    connection = new signalR.HubConnectionBuilder()
        .withUrl(`/hubs/party`, {
            accessTokenFactory: () => getToken() || ''
        })
        .withAutomaticReconnect([0, 1000, 3000, 5000, 10000, 30000])
        .build();

    // === Ивенты ===
    connection.on('EventStarted', (ev) => {
        activeEvents.update((events) => {
            if (events.some((e) => e.sessionId === ev.sessionId)) return events;
            return [...events, ev];
        });
        showToast(`🎉 Новый ивент: ${ev.displayName}`, 'info');

        if (isNotificationSupported() && Notification.permission === 'granted') {
            showBrowserNotification(`🎉 ${ev.displayName}`, {
                body: ev.description || 'Скорее участвуй!',
                tag: `event-${ev.sessionId}`
            });
        }
    });

    connection.on('EventFinished', (data) => {
        activeEvents.update((events) => events.filter((e) => e.sessionId !== data.sessionId));
        showToast('🏁 Ивент завершён', 'info');
    });

    connection.on('ReceiveBroadcast', (message: string) => {
        showToast(`📢 ${message}`, 'info');

        if (isNotificationSupported() && Notification.permission === 'granted') {
            showBrowserNotification('📢 Сообщение от ведущего', {
                body: message,
                tag: 'broadcast'
            });
        }
    });

    connection.on('BalanceUpdated', (data: { balance: number }) => {
        balance.set(data.balance);
    });

    // === Фото и стенка пожеланий ===
    connection.on('PhotoUploaded', (data: { photoId: string; uploadedByName: string }) => {
        photosVersion.update((v) => v + 1);
        showToast(`📸 ${data.uploadedByName} добавил(а) фото`, 'info');
    });

    connection.on('PhotoRemoved', (data: { photoId: string }) => {
        photoRemovedId.set(data.photoId);
    });

    connection.on('WishAdded', () => {
        wishesVersion.update((v) => v + 1);
    });

    connection.on('WishRemoved', (data: { wishId: string }) => {
        wishRemovedId.set(data.wishId);
    });

    // Событие приходит только админам: у них открыты вкладки модерации
    connection.on('ModerationPending', (data: { kind: string }) => {
        moderationVersion.update((v) => v + 1);
        showToast(
            data.kind === 'photo' ? '📸 Новое фото на модерации' : '💌 Новое пожелание на модерации',
            'info'
        );
    });

    // === Шпионаж ===
    connection.on('SpyGameRoleAssigned', (data: any) => {
        console.log('SpyGameRoleAssigned received:', data);
        spyGameRole.set(data);
    });

    connection.on('SpyGameStarted', (data: any) => {
        showToast(`🕵 Началась игра «Шпионаж»! Игроков: ${data.playersCount}`, 'info');
    });

    connection.on('SpyGameFinished', (result: any) => {
        spyGameRole.set(null);
        if (result.winner === 'spies') {
            showToast(`🕵 Шпионы победили! Слово: ${result.secretWord}`, 'info');
        } else if (result.winner === 'town') {
            showToast(`👤 ${result.townWinnerName} разоблачил шпионов! Слово: ${result.secretWord}`, 'info');
        } else {
            showToast(`Ничья. Слово было: ${result.secretWord}`, 'info');
        }
    });

    // === Состояние соединения ===
    connection.onreconnecting(() => connectionState.set('connecting'));
    connection.onreconnected(() => connectionState.set('connected'));
    connection.onclose(() => connectionState.set('disconnected'));

    await connection.start();
    connectionState.set('connected');
}

export async function reconnectIfNeeded(): Promise<void> {
    if (!connection) return;

    const state = connection.state;
    if (state === signalR.HubConnectionState.Disconnected ||
        state === signalR.HubConnectionState.Disconnecting) {
        try {
            await connection.start();
            connectionState.set('connected');
        } catch (e) {
            console.error('Reconnect failed:', e);
            connectionState.set('disconnected');
        }
    }
}

export async function disconnect(): Promise<void> {
    if (connection) {
        await connection.stop();
        connection = null;
    }
    connectionState.set('disconnected');
}