<script lang="ts">
    import { user, balance, connectionState } from '../stores';

    const stateEmoji = {
        connected: '🟢',
        connecting: '🟡',
        disconnected: '🔴'
    };
</script>

<header>
    <div class="header-left">
        <span class="logo">🎉</span>
        <span class="name">{$user?.displayName}</span>
    </div>
    <div class="header-right">
        <span class="balance">⭐ {$balance}</span>
        <span class="conn" title="Соединение">{$connectionState === 'connected' ? '🟢' : ''}</span>
    </div>
</header>