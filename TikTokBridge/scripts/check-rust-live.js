// Opt-in real TikTok check. Uses an isolated server/config; sends no chat or gifts.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const net = require('node:net');
const WebSocket = require('ws');
const { PointsLeaderboard } = require('../src/points-leaderboard');
const repo = path.resolve(__dirname, '../..');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const eventTypes = ['member', 'chat', 'like', 'gift', 'follow', 'share'];

(async () => {
    const username = (process.argv[2] || '').replace(/^@/, '');
    assert.match(username, /^[a-zA-Z0-9_.]{2,24}$/, 'Pass a public LIVE username');
    const seconds = Number(process.argv[3] || 180);
    assert.ok(Number.isFinite(seconds) && seconds >= 30 && seconds <= 1800);
    const expectOffline = process.argv[4] === '--expect-offline';
    const output = await fs.mkdtemp(path.join(repo, 'UnityProject/Logs/real-live-'));
    const config = path.join(output, 'config');
    await fs.mkdir(config);
    for (const file of ['game.json', 'gifts.json', 'master.json', 'observed-gifts.json', 'display.json']) {
        await fs.copyFile(path.join(repo, 'server/config', file), path.join(config, file));
    }
    const listener = net.createServer().listen(0, '127.0.0.1');
    await once(listener, 'listening');
    const port = listener.address().port;
    await new Promise(resolve => listener.close(resolve));
    const binary = process.env.RUST_SERVER_BINARY || path.join(repo, 'server/target/debug/tiktok-server.exe');
    // Keep Cargo's output unlocked on Windows while a live observation runs.
    const testBinary = path.join(output, path.basename(binary));
    await fs.copyFile(binary, testBinary);
    const child = spawn(testBinary, [], { cwd: path.join(repo, 'server'), windowsHide: true, stdio: 'pipe',
        env: { ...process.env, PORT: String(port), HOST: '127.0.0.1', ALLOW_LAN: '0', CONFIG_DIR: config,
            LIVE_PROVIDER: 'tiktok', LOG_TIKTOK_EVENTS: '0', RUST_LOG: 'tiktok_server=info,piratetok_live_rs=info,warn' } });
    let log = '';
    child.stdout.on('data', data => { log += data; });
    child.stderr.on('data', data => { log += data; });
    const report = { username, mode: expectOffline ? 'offline' : 'live', startedAt: new Date().toISOString(), seconds, port, pid: child.pid, output,
        counts: Object.fromEntries(eventTypes.map(type => [type, 0])), statuses: [], snapshots: [],
        totals: { likes: 0, diamonds: 0 }, pointsChecks: 0, duplicates: 0, errors: [] };
    const sockets = [];
    const reference = new PointsLeaderboard();
    const expectedByRevision = new Map([[0, reference.snapshot()]]);
    const ids = new Set();
    let observing = false;
    const trace = [];
    async function until(predicate, limit = 15000) {
        const deadline = Date.now() + limit;
        while (Date.now() < deadline) {
            if (child.exitCode !== null) throw new Error(`Server exited (${child.exitCode})`);
            const value = await predicate(); if (value) return value;
            await pause(50);
        }
        throw new Error('Timeout waiting for live protocol');
    }
    function observe(message) {
        try {
            if (message.type === 'status') report.statuses.push({ at: new Date().toISOString(), ...message });
            if (message.type === 'reset') {
                reference.clear(); ids.clear(); expectedByRevision.clear();
                expectedByRevision.set(0, reference.snapshot());
            }
            if (!observing || !eventTypes.includes(message.type)) return;
            report.counts[message.type]++;
            trace.push(message);
            if (message.eventId) {
                if (ids.has(message.eventId)) report.duplicates++;
                ids.add(message.eventId);
            }
            for (const field of ['likeCount', 'diamondCount', 'repeatCount', 'durationMs']) {
                assert.ok(Number.isFinite(message[field]) && message[field] >= 0, `Invalid ${field}`);
            }
            if (message.type === 'like') report.totals.likes += message.likeCount;
            if (message.type === 'gift') report.totals.diamonds += message.diamondCount;
            if (reference.apply(message)) {
                const expected = reference.snapshot();
                expectedByRevision.set(expected.pointsRevision, expected);
                assert.equal(message.pointsVersion, 1);
                assert.equal(message.pointsRevision, expected.pointsRevision);
                assert.deepEqual(message.pointScores, expected.pointScores);
                report.pointsChecks++;
            }
        } catch (error) { report.errors.push(error.message); }
    }
    async function connect(primary = false) {
        const socket = new WebSocket(`ws://127.0.0.1:${port}`);
        sockets.push(socket);
        const messages = [];
        socket.on('message', data => {
            const message = JSON.parse(data); messages.push(message);
            if (primary) observe(message);
        });
        await once(socket, 'open');
        const send = value => socket.send(JSON.stringify(value));
        send({ type: 'register', role: primary ? 'control' : 'overlay' });
        await until(() => messages.find(m => m.type === (primary ? 'master_config' : 'snapshot')));
        return { socket, messages, send };
    }
    async function checkSnapshot() {
        const overlay = await connect();
        const snapshot = overlay.messages.find(m => m.type === 'snapshot');
        const expected = await until(() => expectedByRevision.get(snapshot.pointsRevision));
        assert.equal(snapshot.pointsVersion, 1);
        assert.deepEqual(snapshot.pointScores, expected.pointScores);
        assert.ok(snapshot.players.every(p => p.userId && !p.userId.startsWith('npc-')));
        report.snapshots.push({ at: new Date().toISOString(), revision: snapshot.pointsRevision, players: snapshot.players.length });
        overlay.socket.terminate();
    }
    try {
        await until(async () => { try { return (await fetch(`http://127.0.0.1:${port}/api/health`)).ok; } catch { return false; } });
        const control = await connect(true);
        observing = true;
        control.send({ type: 'set_username', username });
        if (expectOffline) {
            const status = await until(() => report.statuses.find(s => s.state === 'error'), 30000);
            assert.match(status.message, /KHÔNG live|no active room|status=4/i);
            await until(() => report.statuses.at(-1)?.state === 'reconnecting');
            assert.ok(!report.statuses.some(s => s.state === 'connected'));
            assert.equal(trace.length, 0);
            control.send({ type: 'disconnect_tiktok' });
            await until(() => report.statuses.at(-1)?.state === 'idle');
            report.result = 'PASS';
            return;
        }
        await until(() => report.statuses.find(s => s.state === 'connected'), 45000);
        console.log(JSON.stringify({ connected: username, port, pid: child.pid, output }));
        await checkSnapshot();
        const deadline = Date.now() + seconds * 1000;
        let nextSnapshot = Date.now() + 30000;
        while (Date.now() < deadline) {
            await pause(1000);
            if (child.exitCode !== null) throw new Error('Server stopped during observation');
            if (Date.now() >= nextSnapshot) {
                await checkSnapshot();
                console.log(JSON.stringify({ elapsedSeconds: seconds - Math.round((deadline - Date.now()) / 1000), counts: report.counts,
                    pointsChecks: report.pointsChecks, errors: report.errors.length }));
                nextSnapshot = Date.now() + 30000;
            }
            if (report.statuses.at(-1)?.state === 'ended') break;
        }
        await checkSnapshot();
        control.send({ type: 'disconnect_tiktok' });
        await until(() => report.statuses.at(-1)?.state === 'idle');
        const countAtDisconnect = trace.length;
        await pause(1200);
        assert.equal(trace.length, countAtDisconnect, 'events continue after disconnect');
        // Re-open the real provider through the same operator path. This starts
        // a new scoring session, unlike an overlay-only reconnect above.
        const previousConnected = report.statuses.filter(s => s.state === 'connected').length;
        control.send({ type: 'set_username', username });
        await until(() => report.statuses.filter(s => s.state === 'connected').length > previousConnected, 45000);
        await until(() => trace.length > countAtDisconnect, 30000);
        await checkSnapshot();
        report.manualLiveReconnect = true;
        control.send({ type: 'disconnect_tiktok' });
        await until(() => report.statuses.at(-1)?.state === 'idle');
        assert.ok(trace.length > 0, 'Connected but no gameplay events received');
        assert.equal(report.duplicates, 0);
        assert.deepEqual(report.errors, []);
        report.result = 'PASS';
    } catch (error) {
        report.result = 'FAIL'; report.errors.push(error.message); process.exitCode = 1;
    } finally {
        report.finishedAt = new Date().toISOString();
        report.unobservedEvents = eventTypes.filter(type => report.counts[type] === 0);
        for (const socket of sockets) socket.terminate();
        if (child.exitCode === null) { child.kill(); await once(child, 'exit'); }
        await fs.writeFile(path.join(output, 'server.log'), log);
        await fs.writeFile(path.join(output, 'events.json'), JSON.stringify(trace));
        await fs.writeFile(path.join(output, 'report.json'), JSON.stringify(report, null, 2));
        console.log(JSON.stringify(report));
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
