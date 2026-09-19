const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { DisplaySettings, defaults, applyDisplayPatch } = require('../src/config/display');
const enabled = { showTop: true, showWelcome: true, showChat: true, showFeed: true,
    showGiftEffects: true, focusNpc: true, focusChat: true };

test('display defaults, serialized updates, persistence and failed writes', async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'tiktok-display-'));
    try {
        const file = path.join(directory, 'display.json');
        const store = new DisplaySettings(file);
        assert.deepEqual(store.value, enabled);
        await Promise.all([store.update({ showTop: false }), store.update({ showChat: false })]);
        assert.deepEqual(new DisplaySettings(file).value, { ...enabled, showTop: false, showChat: false });
        for (const key of Object.keys(enabled)) await store.update({ [key]: false });
        assert.deepEqual(new DisplaySettings(file).value, Object.fromEntries(Object.keys(enabled).map(key => [key, false])));
        for (const patch of [null, [], {}, { showTop: 'false' }, { showChat: null }, { unknown: false }]) {
            assert.throws(() => applyDisplayPatch(defaults(), patch));
        }
        const broken = new DisplaySettings(path.join(directory, 'missing', 'display.json'));
        await assert.rejects(broken.update({ showTop: false }));
        assert.deepEqual(broken.value, defaults(), 'failed saves must not change the live config');
        await store.update({ showTop: true });
        assert.equal(new DisplaySettings(file).value.showChat, false);
        await fs.writeFile(file, JSON.stringify({ showTop: false, showWelcome: true, showChat: true }));
        assert.deepEqual(new DisplaySettings(file).value, { ...enabled, showTop: false }, 'existing config enables newly added switches');
    } finally { await fs.rm(directory, { recursive: true, force: true }); }
});
