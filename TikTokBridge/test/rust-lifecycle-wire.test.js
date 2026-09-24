const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const net = require('node:net');
const WebSocket = require('ws');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

test('rust: floor policy, TTL, replay, reconnect, source switching, malformed input and failed config save',
    { timeout: 95000, skip: !process.env.RUST_SERVER_BINARY }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'tiktok-rust-lifecycle-'));
    const repo = path.resolve(__dirname, '../..');
    for (const name of ['game.json', 'gifts.json', 'master.json', 'observed-gifts.json', 'display.json']) {
        await fs.copyFile(path.join(repo, 'server/config', name), path.join(directory, name));
    }
    const game = JSON.parse(await fs.readFile(path.join(directory, 'game.json')));
    await fs.writeFile(path.join(directory, 'game.json'), JSON.stringify({ ...game, playerTtlMs: 1000, maxPlayers: 3 }));
    const listener = net.createServer();
    listener.listen(0, '127.0.0.1'); await once(listener, 'listening');
    const port = listener.address().port;
    await new Promise(resolve => listener.close(resolve));
    const source = new WebSocket.Server({ host: '127.0.0.1', port: 0, autoPong: false });
    await once(source, 'listening');
    let connections = 0;
    let respondToPing = true;
    source.on('connection', socket => {
        connections++;
        socket.on('ping', data => { if (respondToPing) socket.pong(data); });
    });
    const child = spawn(process.env.RUST_SERVER_BINARY, [], { cwd: path.join(repo, 'server'), windowsHide: true,
        env: { ...process.env, CONFIG_DIR: directory, PORT: String(port), HOST: '127.0.0.1', ALLOW_LAN: '0',
            LIVE_PROVIDER: 'tikfinity', TIKFINITY_WS_URL: `ws://127.0.0.1:${source.address().port}` }, stdio: 'pipe' });
    let log = '';
    child.stdout.on('data', data => { log += data; });
    child.stderr.on('data', data => { log += data; });
    const sockets = [];
    async function until(predicate, attempts = 240) {
        for (let i = 0; i < attempts; i++) {
            if (child.exitCode !== null) throw new Error(`Server stopped: ${log}`);
            const value = await predicate(); if (value) return value;
            await delay(25);
        }
        throw new Error(`Timed out: ${log}`);
    }
    async function connect(role = 'overlay', options) {
        const socket = new WebSocket(`ws://127.0.0.1:${port}`, options);
        sockets.push(socket);
        const messages = [];
        socket.on('message', data => messages.push(JSON.parse(data)));
        await once(socket, 'open');
        const send = data => socket.send(JSON.stringify(data));
        send({ type: 'register', role });
        await until(() => messages.find(m => m.type === (role === 'control' ? 'master_config' : 'snapshot')));
        return { socket, messages, send };
    }
    async function snapshot() {
        const client = await connect();
        const result = client.messages.find(m => m.type === 'snapshot');
        client.socket.terminate();
        return result;
    }
    const emit = (feed, event, eventId, userId, extra = {}) => feed.send(JSON.stringify({
        event, eventId, data: { userId, nickname: userId, ...extra }
    }));
    try {
        await until(async () => { try { return (await fetch(`http://127.0.0.1:${port}/api/health`)).ok; } catch { return false; } });
        const control = await connect('control');
        const second = await connect('control');
        const overlay = await connect();
        const browserOverlay = await connect('overlay', { origin: `http://127.0.0.1:${port}` });
        browserOverlay.send({ type: 'reset_game' });
        await until(() => browserOverlay.messages.find(m => m.type === 'error'));

        control.send({ type: 'set_username', username: 'audit_room' });
        let feed = await until(() => [...source.clients][0]);
        await until(() => overlay.messages.find(m => m.type === 'status' && m.state === 'connected'));
        feed.send('invalid JSON');
        feed.send(JSON.stringify({ event: 'unknown' }));
        emit(feed, 'member', 'member-1', 'viewer');
        emit(feed, 'chat', 'chat-1', 'viewer', { comment: 'hello' });
        emit(feed, 'like', 'like-1', 'viewer', { likeCount: 7 });
        await until(() => overlay.messages.find(m => m.eventId === 'like-1'));
        assert.equal(overlay.messages.find(m => m.eventId === 'member-1').spectatorOnly, true);
        assert.equal(overlay.messages.find(m => m.eventId === 'chat-1').spectatorOnly, true);
        assert.equal((await snapshot()).players.length, 0);
        emit(feed, 'chat', 'join-1', 'viewer', { comment: 'hey' });
        await until(() => overlay.messages.find(m => m.eventId === 'join-1'));
        assert.equal(overlay.messages.find(m => m.eventId === 'join-1').joinedNow, true);
        await delay(1100);
        assert.equal((await snapshot()).players.length, 0, 'inactive dancers expire');
        assert.equal((await snapshot()).pointScores[0].points, 7, 'points survive floor TTL');
        emit(feed, 'chat', 'join-2', 'viewer', { comment: 'hey' });
        emit(feed, 'follow', 'follow-1', 'follower');
        emit(feed, 'share', 'share-1', 'sharer');
        emit(feed, 'gift', 'gift-1', 'giver', { giftId: 'demo-audit', diamondCount: 2, repeatCount: 3 });
        await until(() => overlay.messages.find(m => m.eventId === 'gift-1'));
        assert.equal(overlay.messages.find(m => m.eventId === 'join-2').joinedNow, true);
        assert.equal(overlay.messages.find(m => m.eventId === 'follow-1').spectatorOnly, false);
        assert.equal(overlay.messages.find(m => m.eventId === 'share-1').spectatorOnly, false);
        assert.equal((await snapshot()).players.length, 3, 'floor size is capped');

        const previousConnections = connections;
        feed.terminate();
        await until(() => connections > previousConnections);
        feed = [...source.clients][0];
        emit(feed, 'gift', 'gift-1', 'giver', { giftId: 'demo-audit', diamondCount: 2, repeatCount: 3 });
        emit(feed, 'like', 'after-reconnect', 'viewer', { likeCount: 1 });
        await until(() => overlay.messages.find(m => m.eventId === 'after-reconnect'));
        assert.equal((await snapshot()).pointScores.find(p => p.userId === 'giver').points, 600);
        assert.equal((await snapshot()).pointScores.find(p => p.userId === 'viewer').points, 8);

        const batch = Array.from({ length: 1000 }, (_, index) => ({ event: 'like', eventId: `burst-${index}`,
            data: { userId: 'burst-viewer', likeCount: 1 } }));
        feed.send(JSON.stringify(batch));
        feed.send(JSON.stringify(batch.slice(0, 500)));
        await until(() => overlay.messages.find(m => m.eventId === 'burst-999'));
        assert.equal((await snapshot()).pointScores[0].points, 1000);
        assert.equal((await snapshot()).pointScores[0].userId, 'burst-viewer');

        const masterFile = path.join(directory, 'master.json');
        const originalMaster = JSON.parse(await fs.readFile(masterFile));
        const changed = { ...originalMaster, giftAlwaysJoins: false };
        control.send({ type: 'master_save', master: changed });
        await until(() => control.messages.find(m => m.type === 'master_saved'));
        assert.equal(JSON.parse(await fs.readFile(masterFile)).giftAlwaysJoins, false);
        emit(feed, 'gift', 'spectator-gift', 'spectator', { giftId: 'demo-audit', diamondCount: 1 });
        await until(() => overlay.messages.find(m => m.eventId === 'spectator-gift'));
        assert.equal(overlay.messages.find(m => m.eventId === 'spectator-gift').spectatorOnly, true);
        await fs.unlink(masterFile); await fs.mkdir(masterFile);
        control.send({ type: 'master_save', master: originalMaster });
        await until(() => control.messages.find(m => m.type === 'error'));
        const checkMaster = await connect('control');
        assert.equal(checkMaster.messages.find(m => m.type === 'master_config').master.giftAlwaysJoins, false);
        checkMaster.send({ type: 'master_save', master: 'bad' });
        await until(() => checkMaster.messages.find(m => m.type === 'error'));

        control.send({ type: 'set_username', username: 'room_one' });
        second.send({ type: 'set_username', username: 'room_two' });
        await until(() => overlay.messages.some(m => m.type === 'status' && m.state === 'connected' && ['room_one', 'room_two'].includes(m.username)));
        await delay(200);
        assert.equal(source.clients.size, 1, 'concurrent controls leave only one provider connection');
        control.send({ type: 'demo_stop' });
        await delay(100);
        const statusCheck = await connect();
        assert.equal(statusCheck.messages.find(m => m.type === 'status').state, 'connected');
        control.send({ type: 'demo_start', count: 2 });
        await until(() => source.clients.size === 0);
        await until(() => overlay.messages.find(m => m.type === 'member' && m.userId === 'demo-2'));
        assert.ok((await snapshot()).players.every(p => p.userId.startsWith('demo-')));
        control.send({ type: 'demo_stop' });
        control.send({ type: 'disconnect_tiktok' });
        await delay(100);
        assert.equal(source.clients.size, 0);

        const invalid = await connect();
        const closed = once(invalid.socket, 'close');
        invalid.socket.send('{'); invalid.socket.send('{'); invalid.socket.send('{');
        await closed;
        const limited = await connect();
        const rateClosed = once(limited.socket, 'close');
        for (let i = 0; i < 45; i++) limited.send({ type: 'ping' });
        await rateClosed;
        assert.ok((await fetch(`http://127.0.0.1:${port}/api/health`)).ok);
        // A TCP connection can remain open after the provider stops responding.
        respondToPing = false;
        control.send({ type: 'set_username', username: 'heartbeat_room' });
        await until(() => source.clients.size === 1);
        const heartbeatConnections = connections;
        await until(() => connections > heartbeatConnections, 2800);
        assert.equal(child.exitCode, null, 'server recovers from missing provider pongs');
    } finally {
        for (const socket of sockets) socket.terminate();
        for (const client of source.clients) client.terminate();
        await new Promise(resolve => source.close(resolve));
        if (child.exitCode === null) { child.kill(); await once(child, 'exit'); }
        await fs.rm(directory, { recursive: true, force: true });
    }
});
