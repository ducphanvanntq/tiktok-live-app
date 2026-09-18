'use strict';

const POINTS_PER_DIAMOND = 100;
const MAX_POINTS = Number.MAX_SAFE_INTEGER;

// Session-owned scores survive overlay reconnects, independently of floor TTL.
class PointsLeaderboard {
    constructor() { this.clear(); }

    clear() {
        this.players = new Map();
        this.revision = 0;
        this.sequence = 0;
    }

    apply(event) {
        const id = event.userId;
        if (!id || id.startsWith('npc-')) return false;
        const amount = event.type === 'like' ? event.likeCount : event.type === 'gift' ? event.diamondCount : 0;
        const units = Number.isFinite(amount) ? Math.max(0, Math.floor(amount)) : 0;
        const delta = Math.min(MAX_POINTS, units * (event.type === 'gift' ? POINTS_PER_DIAMOND : 1));
        const previous = this.players.get(id);
        if (!previous && delta === 0) return false;
        const nickname = event.nickname?.trim() || previous?.nickname || event.uniqueId || id;
        const avatar = event.avatar || previous?.avatar || '';
        const points = Math.min(MAX_POINTS, (previous?.points || 0) + delta);
        if (previous && points === previous.points && nickname === previous.nickname && avatar === previous.avatar) return false;
        const reachedOrder = !previous || points > previous.points ? ++this.sequence : previous.reachedOrder;
        this.players.set(id, { userId: id, nickname, avatar, points, reachedOrder });
        this.revision += 1;
        return true;
    }

    snapshot() {
        const pointScores = [...this.players.values()]
            .filter(player => player.points > 0)
            .sort((a, b) => b.points - a.points || a.reachedOrder - b.reachedOrder || a.userId.localeCompare(b.userId))
            .slice(0, 3).map(player => ({ ...player }));
        return { pointsVersion: 1, pointsRevision: this.revision, pointScores };
    }
}

module.exports = { PointsLeaderboard };
