import { writable } from 'svelte/store';

export interface UserInfo {
    userId: string;
    username: string;
    displayName: string;
    role: string;
}

export interface ActiveEvent {
    sessionId: string;
    definitionId: string;
    type: string;
    displayName: string;
    description?: string;
    availability: string;
    startedAt: string;
}

export const user = writable<UserInfo | null>(null);
export const balance = writable<number>(0);
export const activeEvents = writable<ActiveEvent[]>([]);
export const connectionState = writable<'disconnected' | 'connecting' | 'connected'>('disconnected');
export const toast = writable<{ message: string; type: 'success' | 'error' | 'info' } | null>(null);
export const spyGameRole = writable<any>(null);

// Инкрементируются при живых событиях SignalR, чтобы лента и стенка обновились
export const photosVersion = writable(0);
export const wishesVersion = writable(0);

// ID удалённых элементов, чтобы убрать их из открытых списков без перезагрузки
export const photoRemovedId = writable<string | null>(null);
export const wishRemovedId = writable<string | null>(null);

// Инкрементируется у админов, когда появляется контент на модерации
export const moderationVersion = writable(0);

// Магазин: товары, покупки и лоты — открытые экраны обновляются без перезагрузки
export const shopVersion = writable(0);

// Подтверждённый админом фант: игрок сразу видит начисленные баллы
export const dareConfirmed = writable<{ sessionId: string; playerId: string; points: number } | null>(null);

// Админ подтвердил клетку бинго — карточки игроков обновляются
export const bingoCellConfirmed = writable<{ sessionId: string; cellIndex: number; confirmedCount: number } | null>(null);

// Админ отклонил клетку бинго — слоты предсказаний освободились
export const bingoCellRejected = writable<{ sessionId: string; cellIndex: number; rejectedCount: number } | null>(null);

// Разыграна линия бинго: бонус получил самый быстрый
export const bingoLineAwarded = writable<{
    sessionId: string;
    lineAwards: { lineIndex: number; lineLabel: string; playerId: string; amount: number }[];
} | null>(null);

// Лототрон: победитель выбран — колесо на экране и карточки игроков обновляются
export const raffleDrawn = writable<{
    sessionId: string;
    winner: { id: string; name: string };
    participants: { id: string; name: string }[];
} | null>(null);

export function showToast(message: string, type: 'success' | 'error' | 'info' = 'success') {
    toast.set({ message, type });
    setTimeout(() => toast.set(null), 3000);
}