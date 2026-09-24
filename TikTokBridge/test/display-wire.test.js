const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const net = require('node:net');
const WebSocket = require('ws');
const enabled = { showTop: true, showWelcome: true, showChat: true, showFeed: true,
    showGiftEffects: true, focusNpc: true, focusChat: true };

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate) {
    for (let i = 0; i < 200; i++) { const result = await predicate(); if (result) return result; await delay(50); }
    throw new Error('Timed out waiting for display settings protocol');
}

for (const kind of ['node', 'rust']) test(`${kind}: display switches broadcast, reject unauthorized writes, survive reconnect/reset/restart`,
    { timeout: 60000, skip: kind === 'rust' && !process.env.RUST_SERVER_BINARY }, async () => {
    const expected = switches => kind === 'rust' ? { ...switches, lighting: {
        floorEnabled: true, backgroundEnabled: true, lightsEnabled: true,
        floorBrightness: 1.5, backgroundBrightness: 1.25, lightsBrightness: 1.5,
        beamWidth: 2.5, floorPalette: 3, backgroundPalette: 1, lightsPalette: 3, floorPattern: 0,
    } } : switches;
    const listener = net.createServer();
    listener.listen(0, '127.0.0.1'); await once(listener, 'listening');
    const port = listener.address().port;
    await new Promise(resolve => listener.close(resolve));
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'tiktok-display-wire-'));
    const configFile = path.join(directory, 'display.json');
    await fs.writeFile(configFile, JSON.stringify({ showTop: true, showWelcome: true, showChat: true }));
    for (const name of ['game.json', 'gifts.json', 'master.json', 'observed-gifts.json']) {
        await fs.copyFile(path.join(__dirname, '..', 'config', name), path.join(directory, name));
    }
    let child;
    let log = '';
    const sockets = [];
    async function start() {
        child = spawn(kind === 'node' ? process.execPath : process.env.RUST_SERVER_BINARY,
            kind === 'node' ? ['server.js'] : [], {
                cwd: path.join(__dirname, '..'), windowsHide: true, stdio: 'pipe',
                env: { ...process.env, PORT: String(port), HOST: '127.0.0.1', ALLOW_LAN: '0',
                    DISPLAY_CONFIG_PATH: configFile, CONFIG_DIR: directory, LIVE_PROVIDER: 'tikfinity' }
            });
        child.stdout.on('data', data => { log += data; });
        child.stderr.on('data', data => { log += data; });
        await until(async () => {
            if (child.exitCode !== null) throw new Error(log);
            try { return (await fetch(`http://127.0.0.1:${port}/api/health`)).ok; } catch { return false; }
        });
    }
    async function stop() {
        for (const socket of sockets.splice(0)) socket.terminate();
        if (child && child.exitCode === null) { child.kill(); await once(child, 'exit'); }
    }
    async function connect(role) {
        const socket = new WebSocket(`ws://127.0.0.1:${port}`);
        sockets.push(socket);
        const messages = [];
        socket.on('message', bytes => messages.push(JSON.parse(bytes)));
        await once(socket, 'open');
        socket.send(JSON.stringify({ type: 'register', role }));
        await until(() => messages.find(m => m.type === 'display_config'));
        return { socket, messages, send: data => socket.send(JSON.stringify(data)) };
    }
    try {
        await start();
        const control = await connect('control');
        const overlay = await connect('overlay');
        const second = await connect('control');
        assert.deepEqual(overlay.messages.find(m => m.type === 'display_config').display,
            expected(enabled));
        overlay.send({ type: 'display_update', patch: { showTop: false } });
        await until(() => overlay.messages.find(m => m.type === 'error'));
        for (const field of Object.keys(enabled)) {
            control.send({ type: 'display_update', patch: { [field]: false } });
            await until(() => overlay.messages.find(m => m.type === 'display_config' && m.display[field] === false));
            await until(() => second.messages.find(m => m.type === 'display_config' && m.display[field] === false));
        }
        const disabled = expected(Object.fromEntries(Object.keys(enabled).map(key => [key, false])));
        assert.deepEqual(JSON.parse(await fs.readFile(configFile, 'utf8')), disabled);
        control.send({ type: 'display_update', patch: { showTop: 'false' } });
        await until(() => control.messages.find(m => m.type === 'display_error'));
        control.send({ type: 'reset_game' });
        await until(() => overlay.messages.find(m => m.type === 'reset'));
        const reconnected = await connect('overlay');
        assert.deepEqual(reconnected.messages.find(m => m.type === 'display_config').display, disabled);
        await stop();
        await start();
        const restarted = await connect('control');
        assert.deepEqual(restarted.messages.find(m => m.type === 'display_config').display, disabled);
        restarted.send({ type: 'display_update', patch: { showChat: true } });
        await until(() => restarted.messages.find(m => m.type === 'display_config' && m.display.showChat));
        assert.deepEqual(JSON.parse(await fs.readFile(configFile, 'utf8')), { ...disabled, showChat: true });
        // A failed atomic replacement reports the previous value and does not broadcast success.
        await fs.unlink(configFile);
        await fs.mkdir(configFile);
        restarted.send({ type: 'display_update', patch: { showTop: true } });
        const error = await until(() => restarted.messages.find(m => m.type === 'display_error'));
        assert.equal(error.display.showTop, false);
    } finally {
        await stop();
        await fs.rm(directory, { recursive: true, force: true });
    }
});
