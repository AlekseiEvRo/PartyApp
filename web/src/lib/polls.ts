/** Общие типы голосования за следующий ивент. */

export interface PollOption {
    id: string;
    definitionId: string;
    displayName: string;
    type: string;
    votes: number;
}

export interface Poll {
    id: string;
    question: string;
    status: string;
    createdAt: string;
    closedAt: string | null;
    options: PollOption[];
    myOptionId: string | null;
    totalVotes: number;
    winnerOptionId: string | null;
}

export function pollPercent(votes: number, total: number): number {
    return total > 0 ? Math.round((votes / total) * 100) : 0;
}

export function pollWinnerName(poll: Poll): string | null {
    if (poll.status !== 'Closed' || !poll.winnerOptionId) return null;
    return poll.options.find((option) => option.id === poll.winnerOptionId)?.displayName ?? null;
}
