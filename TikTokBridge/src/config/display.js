'use strict';

const fs = require('node:fs');
const defaults = () => ({ showTop: true, showWelcome: true, showChat: true,
    showFeed: true, showGiftEffects: true, focusNpc: true, focusChat: true });
const keys = Object.keys(defaults());

function applyDisplayPatch(current, patch) {
    if (!patch || typeof patch !== 'object' || Array.isArray(patch) || !Object.keys(patch).length ||
        Object.entries(patch).some(([key, value]) => !keys.includes(key) || typeof value !== 'boolean')) {
        throw new Error('Cấu hình hiển thị không hợp lệ.');
    }
    return { ...current, ...patch };
}

class DisplaySettings {
    constructor(file) {
        this.file = file;
        this.value = defaults();
        this.pending = Promise.resolve();
        try {
            const saved = JSON.parse(fs.readFileSync(file, 'utf8'));
            for (const key of keys) if (typeof saved?.[key] === 'boolean') this.value[key] = saved[key];
        } catch (error) {
            if (error.code !== 'ENOENT') console.warn(`Không đọc được cài đặt hiển thị: ${error.message}`);
        }
    }

    update(patch) {
        this.pending = this.pending.catch(() => {}).then(async () => {
            const next = applyDisplayPatch(this.value, patch);
            await fs.promises.writeFile(this.file + '.tmp', JSON.stringify(next, null, 2) + '\n', 'utf8');
            await fs.promises.rename(this.file + '.tmp', this.file);
            this.value = next;
            return next;
        });
        return this.pending;
    }
}

module.exports = { DisplaySettings, applyDisplayPatch, defaults };
