import * as signalR from '@microsoft/signalr';
import { activeEvents, connectionState, showToast } from './stores';
import { getToken } from './api';

const API_BASE = import.meta.env.VITE_API_URL || '';

let connection: signalR.HubConnection | null = null;

export function getConnection() {
    return connection;
}

export async function connect(): Promise<void> {
    connectionState.set('connecting');

    connection = new signalR.HubConnectionBuilder()
        .withUrl(`${API_BASE}/hubs/party`, {
            accessTokenFactory: () => getToken() || ''
        })
        .withAutomaticReconnect()
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

    connection.onreconnecting(() => connectionState.set('connecting'));
    connection.onreconnected(() => connectionState.set('connected'));
    connection.onclose(() => connectionState.set('disconnected'));

    await connection.start();
    connectionState.set('connected');
}

export async function disconnect(): Promise<void> {
    if (connection) {
        await connection.stop();
        connection = null;
    }
    connectionState.set('disconnected');
}