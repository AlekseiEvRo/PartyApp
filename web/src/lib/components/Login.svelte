<script lang="ts">
  import { api, setToken } from '../api';
  import { user } from '../stores';

  /** Встроенный режим: без собственного полноэкранного центрирования */
  export let embedded = false;

  let username = '';
  let displayName = '';
  let password = '';
  let isRegister = false;
  let error = '';
  let loading = false;

  async function submit() {
    error = '';
    loading = true;
    try {
      const endpoint = isRegister ? '/api/auth/register' : '/api/auth/login';
      const body = isRegister
        ? { username, displayName, password }
        : { username, password };

      const data = await api<any>(endpoint, 'POST', body);
      setToken(data.accessToken);
      user.set({
        userId: data.userId,
        username: data.username,
        displayName: data.displayName,
        role: data.role
      });
        // Редирект в зависимости от роли и текущего URL
        const isAdminRoute = window.location.pathname.startsWith('/admin')
            || window.location.pathname.startsWith('/screen');
        const admin = data.role === 'Admin' || data.role === 'SuperAdmin';
        if (admin && isAdminRoute) {
            // Остаёмся на админском маршруте
            location.reload();
        } else if (admin && !isAdminRoute) {
            // Админ зашёл через главную — оставляем на главной
        } else if (!admin && isAdminRoute) {
            // Игрок пытается зайти в админку — редирект на главную
            window.location.href = '/';
        }
    } catch (e: any) {
      error = e.message;
    } finally {
      loading = false;
    }
  }
</script>

<div class="login-wrap" class:embedded>
  <div class="login-card">
    <h1>🎉 PartyApp</h1>
    <p class="subtitle">{isRegister ? 'Регистрация' : 'Вход на вечеринку'}</p>

    <form on:submit|preventDefault={submit}>
      <input type="text" placeholder="Логин" bind:value={username} required />
      {#if isRegister}
        <input type="text" placeholder="Отображаемое имя" bind:value={displayName} required />
      {/if}
      <input type="password" placeholder="Пароль" bind:value={password} required />
      <button type="submit" disabled={loading}>
        {loading ? '...' : isRegister ? 'Зарегистрироваться' : 'Войти'}
      </button>
    </form>

    {#if error}
      <p class="error">{error}</p>
    {/if}

    <button class="link" on:click={() => (isRegister = !isRegister)}>
      {isRegister ? 'Уже есть аккаунт? Войти' : 'Нет аккаунта? Регистрация'}
    </button>
  </div>
</div>