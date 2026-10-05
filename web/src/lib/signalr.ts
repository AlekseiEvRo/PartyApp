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
    moderationVersion,
    shopVersion,
    dareConfirmed,
    bingoCellConfirmed,
    bingoCellRejected,
    bingoLineAwarded,
    raffleDrawn,
    profileUpdated,
    achievementsVersion,
    pollUpdated
} from './stores';
import type { Poll } from './polls';
import { getToken, setToken, isTokenExpired } from './api';
import { showBrowserNotification, isNotificationSupported } from './notifications';

let connection: signalR.HubConnection | null = null;

export function getConnection() {
    return connection;
}

export async function connect(): Promise<void> {
    if (connection && connection.state === signalR.HubConnectionState.Connected) {
        return;
    }

    // Истёкший токен не даст подключиться: чистим сессию и показываем вход
    if (isTokenExpired(getToken())) {
        setToken(null);
        window.location.reload();
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
    connection.on('PhotoUploaded', (data: { photoId: string; uploadedByName: string; caption: string | null }) => {
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

    // === Магазин и аукцион ===
    connection.on('ShopUpdated', () => shopVersion.update((v) => v + 1));

    connection.on('PurchaseUpdated', () => shopVersion.update((v) => v + 1));

    connection.on('LotFinished', (data: { name?: string; winnerName?: string; winningBid?: number }) => {
        shopVersion.update((v) => v + 1);

        if (data.winnerName) {
            showToast(`🏆 ${data.name}: победил ${data.winnerName} (${data.winningBid})`, 'info');
        }
    });

    connection.on('LotCancelled', () => shopVersion.update((v) => v + 1));

    connection.on('LotStarted', (data: { lotId?: string; name?: string; minBid?: number }) => {
        shopVersion.update((v) => v + 1);
        showToast(`🔨 Аукцион: ${data.name ?? 'новый лот'}! Скорее делай ставку`, 'info');

        if (isNotificationSupported() && Notification.permission === 'granted') {
            showBrowserNotification(`🔨 ${data.name ?? 'Аукцион'}`, {
                body: `Минимальная ставка — ${data.minBid ?? 1}. Скорее делай ставку!`,
                tag: data.lotId ? `lot-${data.lotId}` : 'auction'
            });
        }
    });

    connection.on('BidPlaced', () => shopVersion.update((v) => v + 1));

    connection.on('DareConfirmed', (data: { sessionId: string; playerId: string; points: number }) => {
        dareConfirmed.set(data);
    });

    connection.on('BingoCellConfirmed', (data: { sessionId: string; cellIndex: number; confirmedCount: number }) => {
        bingoCellConfirmed.set(data);
    });

    connection.on('BingoCellRejected', (data: { sessionId: string; cellIndex: number; rejectedCount: number }) => {
        bingoCellRejected.set(data);
    });

    connection.on('BingoLineAwarded', (data: {
        sessionId: string;
        lineAwards: { lineIndex: number; lineLabel: string; playerId: string; amount: number }[];
    }) => {
        bingoLineAwarded.set(data);
    });

    connection.on('RaffleDrawn', (data: {
        sessionId: string;
        winnerTicket: number;
        ticketsCount: number;
    }) => {
        raffleDrawn.set(data);
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

    // === Профиль ===
    connection.on('ProfileUpdated', (data: { userId: string; profileUpdatedAt: string }) => {
        profileUpdated.set(data);
    });

    // === Достижения ===
    connection.on('AchievementUnlocked', (data: {
        code: string;
        title: string;
        icon: string;
        description: string;
        points: number;
    }) => {
        showToast(`${data.icon} Достижение: ${data.title} (+${data.points})`, 'info');
        achievementsVersion.update((v) => v + 1);

        if (isNotificationSupported() && Notification.permission === 'granted') {
            showBrowserNotification(`${data.icon} ${data.title}`, {
                body: `Достижение! +${data.points} баллов`,
                tag: `achievement-${data.code}`
            });
        }
    });

    // === Голосование за следующий ивент ===
    connection.on('PollUpdated', (poll: Poll) => pollUpdated.set(poll));
    connection.on('PollClosed', (poll: Poll) => pollUpdated.set(poll));

    // === Сессия ===
    // Админ сменил роль, кикнул или заблокировал — токен больше не действует
    connection.on('SessionRevoked', (data: { reason?: string }) => {
        showToast(`🔒 ${data?.reason ?? 'Сессия завершена'}`, 'error');
        setToken(null);

        setTimeout(() => {
            location.reload();
        }, 1500);
    });

    // === Состояние соединения ===
    connection.onreconnecting(() => connectionState.set('connecting'));
    connection.onreconnected(() => {
        connectionState.set('connected');
        // Во время разрыва события могли потеряться — перечитываем магазин
        shopVersion.update((v) => v + 1);
    });
    connection.onclose(() => connectionState.set('disconnected'));

    await connection.start();
    connectionState.set('connected');
}

export async function reconnectIfNeeded(): Promise<void> {
    // Долго открытая вкладка/ТВ: токен мог истечь — тогда нужен повторный вход,
    // иначе соединение вечно переподключается с 401
    if (isTokenExpired(getToken())) {
        setToken(null);
        window.location.reload();
        return;
    }

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