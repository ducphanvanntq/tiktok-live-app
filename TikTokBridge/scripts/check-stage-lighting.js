'use strict';
// Real browser -> Rust WebSocket -> Unity player -> renderer observation.
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const WebSocket = require('ws');
const repo = path.resolve(__dirname, '../..');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(fn, label, timeout = 30000) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) { const value = await fn(); if (value) return value; await pause(100); }
    throw new Error(`Timeout: ${label}`);
}
(async () => {
    const output = path.join(repo, 'docs/mockups/stage-lighting');
    await fs.mkdir(output, { recursive: true });
    await fs.rm(path.join(output, 'stage-state.json'), { force: true });
    const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'stage-lighting-'));
    for (const name of ['game.json', 'gifts.json', 'master.json', 'observed-gifts.json', 'display.json'])
        await fs.copyFile(path.join(repo, 'server/config', name), path.join(temp, name));
    // Use a fresh copy of the old config to verify migration defaults, without changing user settings.
    await fs.writeFile(path.join(temp, 'display.json'), JSON.stringify({ showTop: false }));
    const listener = net.createServer();
    listener.listen(8085, '127.0.0.1'); await once(listener, 'listening'); await new Promise(resolve => listener.close(resolve));
    let server, game, browser, cdp;
    let log = '';
    const sockets = [];
    const checks = [];
    const startServer = async () => {
        server = spawn(process.env.RUST_SERVER_BINARY || path.join(repo, 'server/target/debug/tiktok-server.exe'), [], {
            cwd: path.join(repo, 'server'), windowsHide: true, stdio: 'pipe',
            env: { ...process.env, CONFIG_DIR: temp, PUBLIC_DIR: path.join(repo, 'server/public'), PORT: '8085', HOST: '127.0.0.1', LIVE_PROVIDER: 'tikfinity' }
        });
        server.stdout.on('data', data => { log += data; }); server.stderr.on('data', data => { log += data; });
        await until(async () => { if (server.exitCode !== null) throw Error(log); try { return (await fetch('http://127.0.0.1:8085/api/health')).ok; } catch { return false; } }, 'server startup');
    };
    const stopServer = async () => { const exited = once(server, 'exit'); server.kill(); await exited; };
    try {
        await startServer();
        const profile = path.join(temp, 'edge');
        browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', ['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-port=0','--remote-allow-origins=*','--user-data-dir='+profile,'about:blank'], { windowsHide: true, stdio: 'ignore' });
        const port = await until(async () => { try { return (await fs.readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; } catch { return false; } }, 'browser startup');
        const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
        cdp = new WebSocket(pages.find(page => page.type === 'page').webSocketDebuggerUrl); await once(cdp, 'open');
        let id = 0; const pending = new Map(); const errors = [];
        cdp.on('message', data => { const m = JSON.parse(data); if (m.method === 'Runtime.exceptionThrown') errors.push(m.params); if (m.id) { const callback = pending.get(m.id); pending.delete(m.id); m.error ? callback.reject(m.error) : callback.resolve(m.result); } });
        const send = (method, params = {}) => new Promise((resolve, reject) => { pending.set(++id, { resolve, reject }); cdp.send(JSON.stringify({ id, method, params })); });
        const evaluate = async expression => { const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }); if (result.exceptionDetails) throw Error(JSON.stringify(result.exceptionDetails)); return result.result.value; };
        await send('Page.enable'); await send('Runtime.enable');
        await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1150, deviceScaleFactor: 1, mobile: false });
        await send('Page.navigate', { url: 'http://127.0.0.1:8085/control.html' });
        await until(() => evaluate(`document.querySelector('[data-lighting="floorEnabled"]')?.disabled === false`), 'lighting panel ready');
        await evaluate(`document.querySelector('[data-tab="lighting"]').click()`);
        assert.equal(await evaluate(`document.querySelector('[data-display="showTop"]').checked`), false);
        checks.push('Old display config upgraded without losing existing switches.');
        // Launch visible: minimized/hidden player windows cannot supply reliable screen captures.
        game = spawn(path.join(repo, 'UnityProject/Builds/LedPreview/TikTokBarGame.exe'), ['-stageLightingCapturePath', output, '-logFile', path.join(repo, 'UnityProject/Logs/stage-lighting-wire.log')], { cwd: repo, stdio: 'ignore' });
        const readState = async () => { try { return JSON.parse(await fs.readFile(path.join(output, 'stage-state.json'), 'utf8')); } catch { return null; } };
        const stateFor = async predicate => until(async () => { if (game.exitCode !== null) throw Error('Unity exited early'); const state = await readState(); return state?.connected && predicate(state) ? state : false; }, 'Unity renderer state', 60000);
        await stateFor(s => s.lighting.floorPalette === 3 && s.floorVisible && s.activeBeams === 20);
        const update = async (key, value) => {
            await until(() => evaluate(`!document.querySelector('[data-lighting="${key}"]').disabled`), 'input unlocked');
            await evaluate(`(()=>{const input=document.querySelector('[data-lighting="${key}"]');${typeof value === 'boolean' ? `input.checked=${value}` : `input.value=${JSON.stringify(String(value))}`};input.dispatchEvent(new Event('change',{bubbles:true}));})()`);
            await until(() => evaluate(`!document.querySelector('[data-lighting="${key}"]').disabled`), 'server acknowledgment');
            return stateFor(s => Math.abs(Number(s.lighting[key]) - Number(value)) < .001);
        };
        let state = await update('floorEnabled', false); assert.equal(state.floorVisible, false);
        state = await update('backgroundEnabled', false); assert.equal(state.backgroundVisible, false);
        state = await update('lightsEnabled', false); assert.equal(state.activeLights, 0); assert.equal(state.activeBeams, 0);
        checks.push('All three web switches disable actual Unity renderers/lights independently.');
        await update('floorEnabled', true); await update('backgroundEnabled', true); await update('lightsEnabled', true);
        for (const key of ['floorBrightness','backgroundBrightness','lightsBrightness']) await update(key, 2);
        state = await update('beamWidth', .5); const narrow = state.beamWidth;
        state = await update('beamWidth', 4); assert(state.beamWidth > narrow * 4);
        assert.equal(state.floorBrightness, 2); assert.equal(state.backgroundBrightness, 2);
        checks.push('200% brightness reaches shader materials; beam-width control enlarges real beams.');
        for (let palette = 0; palette < 6; palette++) {
            await evaluate(`document.querySelector('[data-lighting-preset="${palette}"]').click()`);
            await stateFor(s => s.lighting.floorPalette === palette && s.lighting.backgroundPalette === palette + 1 && s.lighting.lightsPalette === palette && s.lighting.beamWidth === 2.5);
        }
        for (let pattern = 0; pattern < 3; pattern++) await update('floorPattern', pattern);
        await update('backgroundPalette', 0);
        checks.push('Six scene palettes, original backdrop colors and three floor patterns apply to Unity.');
        // A client without the control role must not alter lighting.
        const overlay = new WebSocket('ws://127.0.0.1:8085'); sockets.push(overlay); const messages = [];
        overlay.on('message', data => messages.push(JSON.parse(data))); await once(overlay, 'open');
        overlay.send(JSON.stringify({ type: 'register', role: 'overlay' })); await until(() => messages.some(m => m.type === 'snapshot'), 'overlay registration');
        overlay.send(JSON.stringify({ type: 'display_update', patch: { lighting: { lightsEnabled: false } } }));
        await until(() => messages.some(m => m.type === 'error'), 'role rejection');
        checks.push('Overlay clients cannot write lighting settings.');
        const saved = JSON.parse(await fs.readFile(path.join(temp, 'display.json'), 'utf8'));
        assert.equal(saved.lighting.backgroundPalette, 0); assert.equal(saved.showTop, false);
        await stopServer(); await startServer();
        await until(() => evaluate(`!document.querySelector('[data-lighting="backgroundPalette"]').disabled && document.querySelector('[data-lighting="backgroundPalette"]').value === '0'`), 'web reconnect');
        state = await update('floorPalette', 3); assert.equal(state.lighting.backgroundPalette, 0);
        checks.push('Saved values survive Rust restart; browser and Unity reconnect and accept new changes.');
        await update('backgroundPalette', 4); await update('lightsPalette', 3); await update('floorPattern', 0);
        state = await stateFor(s => s.lighting.floorPattern === 0 && s.lighting.floorPalette === 3 && s.lighting.backgroundPalette === 4 && s.lighting.lightsPalette === 3);
        assert.equal(state.npcCount, 20);
        await fs.copyFile(path.join(output, state.screenshot), path.join(output, 'unity-final.png'));
        let screenshot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
        await fs.writeFile(path.join(output, 'web-desktop.png'), Buffer.from(screenshot.data, 'base64'));
        await send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
        assert.equal(await evaluate('document.documentElement.scrollWidth <= innerWidth'), true);
        screenshot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
        await fs.writeFile(path.join(output, 'web-mobile.png'), Buffer.from(screenshot.data, 'base64'));
        assert.equal(errors.length, 0, JSON.stringify(errors));
        checks.push('20 NPCs preserved; responsive web panel has no JavaScript exceptions.');
        await fs.writeFile(path.join(output, 'verification.txt'), checks.map(x => 'PASS: '+x).join('\n')+'\n');
        console.log(checks.map(x => 'PASS: '+x).join('\n'));
        await send('Browser.close');
    } finally {
        sockets.forEach(socket => socket.terminate()); cdp?.close();
        game?.kill(); browser?.kill(); if (server?.exitCode === null) await stopServer();
        await fs.writeFile(path.join(output, 'server-test.log'), log);
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
