const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const net = require('node:net');
const path = require('node:path');
const fs = require('node:fs/promises');
const os = require('node:os');
const WebSocket = require('ws');

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function freePort() {
    const listener = net.createServer();
    listener.listen(0, '127.0.0.1'); await once(listener, 'listening');
    const port = listener.address().port;
    await new Promise(resolve => listener.close(resolve)); return port;
}
async function until(predicate) {
    for (let i = 0; i < 160; i++) { const result = predicate(); if (result) return result; await delay(25); }
    throw new Error('Timed out waiting for points protocol');
}

for (const kind of ['node', 'rust']) test(`${kind}: accepted likes, combo dedupe, reconnect snapshot and reset preserve correct totals`,
    { timeout: 25000, skip: kind === 'rust' && !process.env.RUST_SERVER_BINARY }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'tiktok-points-wire-'));
    for (const name of ['game.json', 'gifts.json', 'master.json', 'observed-gifts.json', 'display.json']) {
        await fs.copyFile(path.join(__dirname, '..', 'config', name), path.join(directory, name));
    }
    const port = await freePort();
    const source = new WebSocket.Server({ host: '127.0.0.1', port: 0 });
    await once(source, 'listening');
    const bridge = spawn(kind === 'node' ? process.execPath : process.env.RUST_SERVER_BINARY,
        kind === 'node' ? ['server.js'] : [], { cwd: path.join(__dirname, '..'), windowsHide: true,
        env: { ...process.env, PORT: String(port), HOST: '127.0.0.1', ALLOW_LAN: '0', LIVE_PROVIDER: 'tikfinity',
            CONFIG_DIR: directory, DISPLAY_CONFIG_PATH: path.join(directory, 'display.json'),
            TIKFINITY_WS_URL: `ws://127.0.0.1:${source.address().port}/`, LOG_TIKTOK_EVENTS: '0' }, stdio: 'pipe' });
    let log = '';
    bridge.stdout.on('data', data => { log += data; });
    bridge.stderr.on('data', data => { log += data; });
    const sockets = [];
    async function connect(role) {
        const socket = new WebSocket(`ws://127.0.0.1:${port}`);
        sockets.push(socket);
        const messages = [];
        socket.on('message', data => messages.push(JSON.parse(data)));
        await once(socket, 'open');
        socket.send(JSON.stringify({ type: 'register', role }));
        await until(() => messages.find(m => m.type === 'config'));
        return { socket, messages };
    }
    try {
        await until(() => /127\.0\.0\.1/.test(log));
        const control = await connect('control');
        control.socket.send(JSON.stringify({ type: 'set_username', username: 'points_test' }));
        const feed = await until(() => [...source.clients][0]);
        const overlay = await connect('overlay');
        assert.deepEqual((await until(() => overlay.messages.find(m => m.type === 'snapshot'))).pointScores, []);
        const user = { userId: 'point-viewer', nickname: 'Minh Anh' };
        const like = { event: 'like', eventId: 'points-like-1', data: { ...user, likeCount: 300 } };
        feed.send(JSON.stringify(like)); feed.send(JSON.stringify(like));
        const scoredLike = await until(() => overlay.messages.find(m => m.type === 'like'));
        assert.equal(scoredLike.pointScores[0].points, 300, 'live update carries the absolute total');
        assert.equal(scoredLike.pointsRevision, 1);
        // Running combo updates must not add the cumulative price repeatedly.
        feed.send(JSON.stringify({ event: 'gift', eventId: 'points-pending', data: { ...user,
            giftId: 'demo-points', giftType: 1, repeatEnd: false, repeatCount: 4, diamondCount: 1 } }));
        const gift = { event: 'gift', eventId: 'points-final', data: { ...user,
            giftId: 'demo-points', giftType: 1, repeatEnd: true, repeatCount: 5, diamondCount: 1 } };
        feed.send(JSON.stringify(gift)); feed.send(JSON.stringify(gift));
        const scoredGift = await until(() => overlay.messages.find(m => m.type === 'gift'));
        assert.equal(scoredGift.pointScores[0].points, 800);
        const reconnected = await connect('overlay');
        const snapshot = await until(() => reconnected.messages.find(m => m.type === 'snapshot'));
        assert.equal(snapshot.pointScores[0].points, 800);
        assert.equal(snapshot.vipScores[0].score, 5, 'raw gift metric remains diamonds');
        assert.equal(snapshot.pointsRevision, 2);
        assert.equal(overlay.messages.filter(m => m.type === 'like').length, 1);
        assert.equal(overlay.messages.filter(m => m.type === 'gift').length, 1);
        assert.equal(overlay.messages.find(m => m.type === 'like').spectatorOnly, true);
        // Unity's manual VIP buttons must use the same authoritative path as
        // the control panel, otherwise connected overlays ignore local gifts.
        feed.send(JSON.stringify({ event: 'member', eventId: 'ordinary-member',
            data: { userId: 'ordinary-viewer', nickname: 'Viewer' } }));
        const ordinaryMember = await until(() => overlay.messages.find(m => m.type === 'member' && m.userId === 'ordinary-viewer'));
        assert.equal(ordinaryMember.spectatorOnly, true, 'normal joins still require the keyword');
        overlay.socket.send(JSON.stringify({ type: 'demo_event', action: 'member', manualUsername: 'Khách VIP' }));
        const manualMember = await until(() => overlay.messages.find(m => m.type === 'member' && m.userId === 'Khách VIP'));
        assert.equal(manualMember.joinedNow, true);
        assert.equal(manualMember.spectatorOnly, false);
        overlay.socket.send(JSON.stringify({ type: 'demo_event', action: 'gift',
            manualUsername: 'Khách VIP', giftName: 'Hoa hồng', value: 1 }));
        const manualGift = await until(() => overlay.messages.find(m => m.type === 'gift' && m.userId === 'Khách VIP'));
        assert.equal(manualGift.pointScores.find(p => p.userId === 'Khách VIP').points, 100);
        const manualReconnect = await connect('overlay');
        const manualSnapshot = await until(() => manualReconnect.messages.find(m => m.type === 'snapshot'));
        assert.equal(manualSnapshot.pointScores.find(p => p.userId === 'Khách VIP').points, 100);
        control.socket.send(JSON.stringify({ type: 'reset_game' }));
        await until(() => overlay.messages.find(m => m.type === 'reset'));
        const afterReset = await connect('overlay');
        assert.deepEqual((await until(() => afterReset.messages.find(m => m.type === 'snapshot'))).pointScores, []);
    } finally {
        for (const socket of sockets) socket.terminate();
        for (const client of source.clients) client.terminate();
        await new Promise(resolve => source.close(resolve));
        if (bridge.exitCode === null) { bridge.kill(); await once(bridge, 'exit'); }
        await fs.rm(directory, { recursive: true, force: true });
    }
});
