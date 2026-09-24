const test = require('node:test');
const assert = require('node:assert/strict');
const { PointsLeaderboard } = require('../src/points-leaderboard');

test('points: empty, free likes, gifts, combo totals and no NPC/zero entries', () => {
    const scores = new PointsLeaderboard();
    assert.deepEqual(scores.snapshot().pointScores, []);
    scores.apply({ type: 'chat', userId: 'a', comment: 'hi' });
    scores.apply({ type: 'gift', userId: 'npc-000', diamondCount: 500 });
    scores.apply({ type: 'like', userId: 'zero', likeCount: 0 });
    assert.deepEqual(scores.snapshot().pointScores, []);
    scores.apply({ type: 'like', userId: 'a', nickname: 'Minh Anh', likeCount: 300, spectatorOnly: true });
    scores.apply({ type: 'gift', userId: 'a', diamondCount: 5, repeatCount: 5 });
    assert.equal(scores.snapshot().pointScores[0].points, 800);
    assert.equal(scores.snapshot().pointsRevision, 2);
});

test('points: stable ties, takeover, only top three, rename and reset', () => {
    const scores = new PointsLeaderboard();
    for (const userId of ['b', 'a', 'c', 'd']) scores.apply({ type: 'like', userId, likeCount: 100 });
    assert.deepEqual(scores.snapshot().pointScores.map(p => p.userId), ['b', 'a', 'c']);
    scores.apply({ type: 'gift', userId: 'd', diamondCount: 1 });
    assert.deepEqual(scores.snapshot().pointScores.map(p => p.userId), ['d', 'b', 'a']);
    scores.apply({ type: 'chat', userId: 'd', nickname: 'Tên mới', avatar: 'https://example.com/avatar.png' });
    assert.equal(scores.snapshot().pointScores[0].nickname, 'Tên mới');
    assert.equal(scores.snapshot().pointScores[0].points, 200);
    const copy = scores.snapshot();
    copy.pointScores[0].points = 0;
    assert.equal(scores.snapshot().pointScores[0].points, 200);
    scores.clear();
    assert.deepEqual(scores.snapshot(), { pointsVersion: 1, pointsRevision: 0, pointScores: [] });
});

test('points: ignores nonfinite/negative counts and caps safe integer totals', () => {
    const scores = new PointsLeaderboard();
    for (const likeCount of [-1, NaN, Infinity]) scores.apply({ type: 'like', userId: 'a', likeCount });
    assert.equal(scores.snapshot().pointScores.length, 0);
    scores.apply({ type: 'gift', userId: 'a', diamondCount: Number.MAX_SAFE_INTEGER });
    scores.apply({ type: 'like', userId: 'a', likeCount: 100 });
    assert.equal(scores.snapshot().pointScores[0].points, Number.MAX_SAFE_INTEGER);
});
