let token: string | null = localStorage.getItem('party_token');

export function setToken(t: string | null) {
    token = t;
    if (t) localStorage.setItem('party_token', t);
    else localStorage.removeItem('party_token');
}

export function getToken() {
    return token;
}

/** Токен отсутствует, повреждён или уже истёк. */
export function isTokenExpired(t: string | null = token): boolean {
    if (!t) return true;
    try {
        const payload = JSON.parse(atob(t.split('.')[1]));
        return Date.now() > payload.exp * 1000;
    } catch {
        return true;
    }
}

export async function api<T>(url: string, method = 'GET', body?: unknown): Promise<T> {
    const res = await fetch(url, {
        method,
        headers: {
            'Content-Type': 'application/json',
            ...(token ? { Authorization: `Bearer ${token}` } : {})
        },
        body: body ? JSON.stringify(body) : undefined
    });

    // 401 = токен истёк или отозван (неверный логин приходит как 400).
    // Чистим сессию и показываем экран входа, иначе страница «залипает»
    // с ошибками на каждом запросе.
    if (res.status === 401) {
        setToken(null);
        window.location.reload();
        throw new Error('Сессия истекла — войди заново');
    }

    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        throw new Error(err.error || `HTTP ${res.status}`);
    }
    return res.json();
}