'use strict';
const lightingFields = [...document.querySelectorAll('[data-lighting]')];
const lightingMessage = document.getElementById('lighting-message');
const lightingPalettes = ['Neon tím / cyan', 'Hoàng hôn vàng / hồng', 'Xanh băng', 'Cầu vồng chuyển màu', 'Hồng kẹo / tím', 'Xanh ngọc / vàng'];
let lightingSaved = null;
let lightingTimer;
for (const key of ['floorPalette', 'lightsPalette', 'backgroundPalette']) {
    const select = document.querySelector(`[data-lighting="${key}"]`);
    const labels = key === 'backgroundPalette' ? ['Màu ảnh gốc', ...lightingPalettes] : lightingPalettes;
    labels.forEach((label, index) => select.add(new Option(label, String(index))));
}
function lightingOutput(input) {
    const output = document.querySelector(`[data-lighting-output="${input.dataset.lighting}"]`);
    if (output) output.textContent = input.dataset.lighting === 'beamWidth' ? `${Number(input.value).toFixed(1)}×` : `${Math.round(Number(input.value) * 100)}%`;
}
function renderLighting(config, message) {
    clearTimeout(lightingTimer);
    if (!config) { lockLighting('Máy chủ chưa hỗ trợ điều khiển ánh sáng. Hãy chạy bản server mới.'); return; }
    lightingSaved = { ...config };
    for (const input of lightingFields) {
        if (input.type === 'checkbox') input.checked = config[input.dataset.lighting] === true;
        else input.value = config[input.dataset.lighting];
        input.disabled = false;
        lightingOutput(input);
    }
    document.querySelectorAll('[data-lighting-preset]').forEach(button => { button.disabled = false; });
    lightingMessage.textContent = message || 'Đã lưu và gửi cài đặt đến game.';
}
function lockLighting(message) {
    clearTimeout(lightingTimer);
    lightingFields.forEach(input => { input.disabled = true; });
    document.querySelectorAll('[data-lighting-preset]').forEach(button => { button.disabled = true; });
    lightingMessage.textContent = message;
}
function saveLighting(patch) {
    if (!lightingSaved || socket?.readyState !== WebSocket.OPEN) { lockLighting('Chờ kết nối máy chủ.'); return; }
    lockLighting('Đang lưu ánh sáng…');
    send({ type: 'display_update', patch: { lighting: patch } });
    lightingTimer = setTimeout(() => { lockLighting('Đang kết nối lại để kiểm tra cài đặt đã lưu…'); socket.close(); }, 8000);
}
lightingFields.forEach(input => {
    input.addEventListener('input', () => lightingOutput(input));
    input.addEventListener('change', () => saveLighting({ [input.dataset.lighting]: input.type === 'checkbox' ? input.checked : Number(input.value) }));
});
lightingPalettes.forEach((label, index) => {
    const button = document.createElement('button');
    button.className = 'btn'; button.dataset.lightingPreset = index; button.textContent = label; button.disabled = true;
    button.addEventListener('click', () => saveLighting({ floorPalette: index, backgroundPalette: index + 1, lightsPalette: index, floorBrightness: 1.5, backgroundBrightness: 1.25, lightsBrightness: 1.5, beamWidth: 2.5 }));
    document.getElementById('lighting-presets').append(button);
});
