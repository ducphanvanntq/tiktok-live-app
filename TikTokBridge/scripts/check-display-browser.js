const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const net = require('node:net');
const WebSocket = require('ws');
const repo = path.resolve(__dirname, '../..');
const delay = ms => new Promise(r => setTimeout(r, ms));
async function until(fn) {
    for (let i = 0; i < 200; i++) { const r = await fn(); if (r) return r; await delay(50); }
    throw new Error('Browser check timeout');
}
(async () => {
    const outputDirectory = process.env.DISPLAY_TEST_OUTPUT || path.join(repo, 'UnityProject', 'Logs');
    await fs.mkdir(outputDirectory, { recursive: true });
    const root = await fs.mkdtemp(path.join(outputDirectory, 'display-browser-'));
    const listener = net.createServer().listen(0, '127.0.0.1');
    await once(listener, 'listening');
    const port = listener.address().port;
    await new Promise(resolve => listener.close(resolve));
    const bridge = spawn(process.execPath, ['server.js'], { cwd: path.join(repo, 'TikTokBridge'), windowsHide: true,
        env: { ...process.env, PORT: String(port), DISPLAY_CONFIG_PATH: path.join(root, 'display.json') }, stdio: 'ignore' });
    let edge, cdp, overlay;
    try {
        await until(async () => { try { return (await fetch(`http://127.0.0.1:${port}/api/health`)).ok; } catch { return false; } });
        const profile = path.join(root, 'profile');
        edge = spawn(process.env.DISPLAY_BROWSER || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
            ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0',
                '--remote-allow-origins=*', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
        const debugPort = await until(async () => { try { return (await fs.readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; } catch { return false; } });
        const pages = await (await fetch(`http://127.0.0.1:${debugPort}/json/list`)).json();
        cdp = new WebSocket(pages.find(p => p.type === 'page').webSocketDebuggerUrl);
        await once(cdp, 'open');
        let id = 0;
        const pending = new Map();
        cdp.on('message', data => { const m = JSON.parse(data); if (m.id) { const cb = pending.get(m.id); pending.delete(m.id); if (m.error) cb?.reject(m.error); else cb?.resolve(m.result); } });
        const send = (method, params = {}) => new Promise((resolve, reject) => {
            pending.set(++id, { resolve, reject }); cdp.send(JSON.stringify({ id, method, params }));
        });
        const evaluate = async expression => {
            const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
            if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
            return result.result.value;
        };
        const state = () => evaluate("[...document.querySelectorAll('[data-display]')].map(e=>({key:e.dataset.display,checked:e.checked,disabled:e.disabled}))");
        await send('Page.enable');
        await send('Emulation.setDeviceMetricsOverride', { width: 1200, height: 1500, deviceScaleFactor: 1, mobile: false });
        await send('Page.navigate', { url: `http://127.0.0.1:${port}/control.html` });
        await until(async () => { const s = await state(); return s.length === 7 && s.every(x => !x.disabled); });
        assert((await state()).every(x => x.checked));
        overlay = new WebSocket(`ws://127.0.0.1:${port}`);
        const messages = [];
        overlay.on('message', data => messages.push(JSON.parse(data)));
        await once(overlay, 'open'); overlay.send(JSON.stringify({ type: 'register', role: 'overlay' }));
        const click = async key => {
            const point = await evaluate(`(()=>{const e=document.querySelector('[data-display="${key}"]');e.scrollIntoView({block:'center'});const r=e.nextElementSibling.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()`);
            await send('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...point });
            await send('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...point });
            await until(async () => (await state()).every(x => !x.disabled));
        };
        for (const key of ['showTop', 'showWelcome', 'showChat', 'showFeed', 'showGiftEffects', 'focusNpc', 'focusChat']) {
            await click(key);
            assert.equal((await state()).find(x => x.key === key).checked, false);
            await until(() => messages.find(x => x.type === 'display_config' && x.display[key] === false));
        }
        await send('Page.reload');
        await until(async () => { const s = await state(); return s.length === 7 && s.every(x => !x.disabled && !x.checked); });
        // Native keyboard input can toggle a switch as well.
        await evaluate("document.getElementById('display-chat').focus()");
        await send('Input.dispatchKeyEvent', { type: 'keyDown', key: ' ', code: 'Space', windowsVirtualKeyCode: 32 });
        await send('Input.dispatchKeyEvent', { type: 'keyUp', key: ' ', code: 'Space', windowsVirtualKeyCode: 32 });
        await until(async () => (await state()).find(x => x.key === 'showChat')?.checked && (await state()).every(x => !x.disabled));
        await evaluate("document.querySelector('.display-settings').scrollIntoView({block:'center'})");
        const shot = await send('Page.captureScreenshot', { format: 'png' });
        await fs.writeFile(path.join(root, 'web-switches.png'), Buffer.from(shot.data, 'base64'));
        assert.deepEqual(JSON.parse(await fs.readFile(path.join(root, 'display.json'), 'utf8')), { showTop: false, showWelcome: false, showChat: true, showFeed: false, showGiftEffects: false, focusNpc: false, focusChat: false });
        bridge.kill(); await once(bridge, 'exit');
        await until(async () => (await state()).every(x => x.disabled));
        const results = 'PASS: native browser clicks and Space keyboard; all default enabled; seven independent switches; broadcast to overlay; persistence after reload; disabled controls on disconnect.\n';
        await fs.writeFile(path.join(root, 'verification.txt'), results);
        console.log(results + root);
        await send('Browser.close').catch(() => {});
    } finally {
        overlay?.terminate(); cdp?.terminate();
        if (edge?.exitCode === null) edge.kill();
        if (bridge.exitCode === null) bridge.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
