let token: string | null = localStorage.getItem('party_token');

export function setToken(t: string | null) {
    token = t;
    if (t) localStorage.setItem('party_token', t);
    else localStorage.removeItem('party_token');
}

export function getToken() {
    return token;
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

    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        throw new Error(err.error || `HTTP ${res.status}`);
    }
    return res.json();
}