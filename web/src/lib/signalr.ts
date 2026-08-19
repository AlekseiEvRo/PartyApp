import * as signalR from '@microsoft/signalr';
import { activeEvents, connectionState, showToast, balance } from './stores';
import { getToken } from './api';

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

    connection.on('EventStarted', (ev) => {
        activeEvents.update((events) => {
            if (events.some((e) => e.sessionId === ev.sessionId)) return events;
            return [...events, ev];
        });
        showToast(`🎉 Новый ивент: ${ev.displayName}`, 'info');
    });

    connection.on('EventFinished', (data) => {
        activeEvents.update((events) => events.filter((e) => e.sessionId !== data.sessionId));
        showToast('🏁 Ивент завершён', 'info');
    });

    connection.on('BalanceUpdated', (data: { balance: number }) => {
        balance.set(data.balance);
    });

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