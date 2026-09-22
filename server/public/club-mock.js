(() => {
  'use strict';
  const canvas = document.querySelector('#club');
  const ctx = canvas.getContext('2d');
  const palettes = { neon: { a: 276, b: 185, label: 'NEON NIGHTS' }, ember: { a: 329, b: 36, label: 'GOLDEN HOUR' }, ice: { a: 216, b: 182, label: 'ICE CLUB' } };
  const state = { preset: 'neon', pattern: 'wave', brightness: .8, tempo: 120, beams: true, lasers: true, crowd: true, paused: matchMedia('(prefers-reduced-motion: reduce)').matches, time: 0, drop: 0 };
  let width = 0, height = 0, last = 0;
  const color = (h, l = 60, a = 1) => `hsla(${h},90%,${l}%,${a})`;
  function resize() {
    const box = canvas.getBoundingClientRect();
    width = box.width; height = box.height;
    const ratio = Math.min(devicePixelRatio || 1, 2);
    canvas.width = Math.round(width * ratio); canvas.height = Math.round(height * ratio);
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    render();
  }
  function polygon(points, fill, stroke, line = 1) {
    ctx.beginPath(); points.forEach(([x, y], i) => i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)); ctx.closePath();
    if (fill) { ctx.fillStyle = fill; ctx.fill(); }
    if (stroke) { ctx.strokeStyle = stroke; ctx.lineWidth = line; ctx.stroke(); }
  }
  function line(x1, y1, x2, y2, stroke, weight = 1) {
    ctx.beginPath(); ctx.moveTo(x1, y1); ctx.lineTo(x2, y2); ctx.strokeStyle = stroke; ctx.lineWidth = weight; ctx.stroke();
  }
  function glow(x, y, radius, hue, opacity) {
    const g = ctx.createRadialGradient(x, y, 0, x, y, radius);
    g.addColorStop(0, color(hue, 60, opacity)); g.addColorStop(1, color(hue, 40, 0));
    ctx.fillStyle = g; ctx.fillRect(x - radius, y - radius, radius * 2, radius * 2);
  }
  function floorPoint(x, z) { const k = .28 + z * .94; return [width / 2 + x * width * k, height * (.565 + .38 * z * z)]; }
  function dancer(x, z, seed, hue) {
    const [px, py] = floorPoint(x, z), s = (.34 + z * .8) * Math.min(width / 850, 1.3), t = state.time;
    const sway = Math.sin(t * 2.1 + seed) * 5 * s, bob = Math.sin(t * 4.2 + seed) * 2 * s;
    glow(px, py, 26 * s, hue, .18);
    ctx.save(); ctx.translate(px + sway, py + bob); ctx.scale(s, s); ctx.lineCap = 'round';
    ctx.shadowColor = color(hue, 60, .5); ctx.shadowBlur = 6;
    const pose = Math.sin(t * 2.1 + seed);
    line(-4, -35, -12 - pose * 4, 0, '#111022', 10); line(4, -35, 15 + pose * 4, 0, '#171227', 10);
    line(0, -64, 0, -34, '#242036', 23);
    line(-9, -60, -23, -45 - pose * 10, '#252038', 8);
    line(-23, -45 - pose * 10, -30, -66 - pose * 20, '#3b2f49', 6);
    line(9, -60, 23, -66 + pose * 12, '#252038', 8);
    line(23, -66 + pose * 12, 29, -83 + pose * 15, '#3b2f49', 6);
    ctx.fillStyle = '#40304c'; ctx.beginPath(); ctx.arc(0, -81, 11, 0, Math.PI * 2); ctx.fill();
    line(-10, -62, -11, -40, color(hue, 66, .7), 1.5); line(-8, -86, -8, -78, color(hue, 75, .8), 1.5);
    ctx.restore();
  }
  function render() {
    if (!width || !height) return;
    const w = width, h = height, t = state.time, p = palettes[state.preset];
    ctx.clearRect(0, 0, w, h); ctx.fillStyle = '#080810'; ctx.fillRect(0, 0, w, h);
    const bg = ctx.createLinearGradient(0, 0, 0, h); bg.addColorStop(0, '#090a14'); bg.addColorStop(.52, '#171020'); bg.addColorStop(1, '#090a12'); ctx.fillStyle = bg; ctx.fillRect(0, 0, w, h);
    glow(w * .5, h * .41, w * .56, p.a, .12); glow(w * .8, h * .52, w * .4, p.b, .07);
    // Receding ceiling ribs and wall panels frame the stage.
    for (let i = 0; i < 6; i++) {
      const inset = i * .067;
      polygon([[w * inset, h * (.07 + inset * .6)], [w * (1 - inset), h * (.07 + inset * .6)], [w * (1 - inset), h * .55], [w * inset, h * .55]], null, '#75628512');
      line(w * inset, h * (.07 + inset * .6), w * .38, h * .26, '#ad8bb217');
      line(w * (1 - inset), h * (.07 + inset * .6), w * .62, h * .26, '#ad8bb217');
    }
    for (const side of [.085, .915]) {
      ctx.fillStyle = '#11101a'; ctx.fillRect(w * side - 14, h * .32, 28, h * .27);
      for (let j = 0; j < 3; j++) { ctx.strokeStyle = '#57505f55'; ctx.lineWidth = 2; ctx.beginPath(); ctx.arc(w * side, h * (.37 + j * .075), 9, 0, Math.PI * 2); ctx.stroke(); }
    }
    // Perspective LED tiles; animation changes their emission rather than flashing the screen.
    for (let row = 0; row < 12; row++) for (let col = 0; col < 16; col++) {
      const x = (col - 8) / 14, z = row / 12;
      const wave = state.pattern === 'checker' ? ((col + row) % 2 ? .82 : .18) : state.pattern === 'ripple' ? (Math.sin(Math.hypot(col - 7.5, row - 5) * 1.1 - t * 2) + 1) / 2 : (Math.sin(col * .6 + row * .7 - t * 1.6) + 1) / 2;
      const hue = wave > .52 ? p.a : p.b, emission = state.brightness * (.16 + wave * .63 + state.drop * .15);
      const pts = [floorPoint(x + .002, z + .009), floorPoint(x + 1 / 14 - .002, z + .009), floorPoint(x + 1 / 14 - .002, z + 1 / 12 - .009), floorPoint(x + .002, z + 1 / 12 - .009)];
      polygon(pts, color(hue, 28 + wave * 22, emission), color(hue, 65, emission * .8), .7);
      const center = floorPoint(x + .035, z + .045);
      if (wave > .83) glow(center[0], center[1], 18 + row * 2, hue, emission * .1);
    }
    polygon([floorPoint(-.574, 0), floorPoint(.574, 0), floorPoint(.574, 1), floorPoint(-.574, 1)], null, color(p.a, 68, .35), 2);
    // Low riser and luminous front of the DJ desk.
    polygon([[w * .25, h * .545], [w * .75, h * .545], [w * .81, h * .58], [w * .19, h * .58]], '#11101c', '#78658040');
    line(w * .19, h * .581, w * .81, h * .581, color(p.a, 70, .6), 2);
    ctx.fillStyle = '#14101e'; ctx.fillRect(w * .34, h * .465, w * .32, h * .078);
    line(w * .34, h * .465, w * .66, h * .465, color(p.b, 75, .8), 2);
    for (let i = 0; i < 36; i++) {
      const bar = (Math.sin(i * .8 + t * 2.5) + 1.5) * h * .008;
      ctx.fillStyle = color(i % 4 ? p.a : p.b, 60, .7); ctx.fillRect(w * (.36 + i * .0078), h * .528 - bar, Math.max(2, w * .003), bar);
    }
    ctx.fillStyle = '#31243f'; ctx.beginPath(); ctx.arc(w * .5, h * .421, h * .014, 0, Math.PI * 2); ctx.fill();
    polygon([[w * .488, h * .437], [w * .512, h * .437], [w * .525, h * .462], [w * .475, h * .462]], '#241b33');
    line(w * .485, h * .445, w * .466, h * .457, '#806088', 3); line(w * .515, h * .445, w * .536, h * .454, '#806088', 3);
    // Overhead truss, fixtures and soft moving cones.
    line(w * .06, h * .112, w * .94, h * .112, '#4b425c', 3);
    line(w * .06, h * .131, w * .94, h * .131, '#383345', 2);
    for (let i = 0; i < 20; i++) line(w * (.06 + i * .044), h * .112, w * (.104 + i * .044), h * .131, '#4b425c55');
    for (let i = 0; i < 6; i++) {
      const x = w * (.12 + i * .152), y = h * .151, hue = i % 2 ? p.b : p.a;
      if (state.beams) {
        const target = x + Math.sin(t * .44 + i * 1.7) * w * .28, bottom = h * (.66 + .1 * Math.sin(i)), spread = w * (.06 + state.drop * .04);
        ctx.save(); ctx.globalCompositeOperation = 'screen';
        const beam = ctx.createLinearGradient(x, y, target, bottom); beam.addColorStop(0, color(hue, 76, .48)); beam.addColorStop(.2, color(hue, 62, .13)); beam.addColorStop(1, color(hue, 50, 0));
        polygon([[x - 3, y], [x + 3, y], [target + spread, bottom], [target - spread, bottom]], beam);
        glow(target, bottom, w * .08, hue, .13); ctx.restore();
      }
      ctx.fillStyle = '#252333'; ctx.fillRect(x - 8, y - 15, 16, 17); ctx.fillStyle = color(hue, 82, state.beams ? 1 : .12); ctx.fillRect(x - 5, y - 1, 10, 3);
      if (state.beams) glow(x, y, 16, hue, .5);
    }
    if (state.lasers) {
      ctx.save(); ctx.globalCompositeOperation = 'screen';
      for (let i = 0; i < 5; i++) {
        const motion = Math.sin(t * .22 + i * .3) * .05;
        line(w * .035, h * .43, w * (.63 + motion + i * .07), h * (.2 + i * .078), color(p.b, 65, .15), .6);
        line(w * .965, h * .43, w * (.37 - motion - i * .07), h * (.2 + i * .078), color(p.a, 65, .2), .6);
      }
      for (let i = 0; i < 65; i++) {
        const x = ((Math.sin(i * 127.1) * 43758.5 % 1 + 1) % 1) * w;
        const y = ((i * .117 + t * .012) % .65) * h + h * .14;
        ctx.fillStyle = color(i % 2 ? p.a : p.b, 83, .12 + .22 * (Math.sin(t + i) + 1) / 2); ctx.fillRect(x, y, i % 5 === 0 ? 2 : 1, i % 5 === 0 ? 2 : 1);
      }
      ctx.restore();
    }
    if (state.crowd) {
      for (let i = 0; i < 17; i++) {
        const row = Math.floor(i / 6), x = ((i % 6) - 2.5) * .15 + Math.sin(i * 53) * .028;
        dancer(x, .21 + row * .205 + (i % 2) * .06, i * 2.37, i % 3 ? p.a : p.b);
      }
    }
    if (state.drop > 0) {
      ctx.save(); ctx.globalCompositeOperation = 'screen'; glow(w * .5, h * .54, w * .7, p.a, state.drop * .22);
      ctx.strokeStyle = color(p.b, 75, state.drop * .7); ctx.lineWidth = 2;
      ctx.beginPath(); ctx.ellipse(w / 2, h * .68, w * (1 - state.drop) * .65 + 10, h * (1 - state.drop) * .17 + 2, 0, 0, Math.PI * 2); ctx.stroke(); ctx.restore();
    }
    const fade = ctx.createLinearGradient(0, h * .72, 0, h); fade.addColorStop(0, '#09081000'); fade.addColorStop(1, '#090810f2'); ctx.fillStyle = fade; ctx.fillRect(0, h * .72, w, h * .28);
  }
  function frame(now) {
    const elapsed = last ? Math.min((now - last) / 1000, .05) : 0; last = now;
    if (!state.paused && !document.hidden) { state.time += elapsed * state.tempo / 120; state.drop = Math.max(0, state.drop - elapsed / 2.5); render(); }
    requestAnimationFrame(frame);
  }
  document.querySelectorAll('[data-preset]').forEach(button => button.addEventListener('click', () => {
    state.preset = button.dataset.preset;
    document.querySelectorAll('[data-preset]').forEach(item => { const active = item === button; item.classList.toggle('active', active); item.setAttribute('aria-pressed', String(active)); });
    document.querySelector('#look-label').textContent = palettes[state.preset].label;
    document.querySelector('#status').textContent = `Đang xem phối màu ${button.querySelector('strong').textContent}.`; render();
  }));
  document.querySelectorAll('[data-pattern]').forEach(button => button.addEventListener('click', () => {
    state.pattern = button.dataset.pattern;
    document.querySelectorAll('[data-pattern]').forEach(item => { const active = item === button; item.classList.toggle('active', active); item.setAttribute('aria-pressed', String(active)); }); render();
  }));
  for (const id of ['beams', 'lasers', 'crowd']) document.getElementById(id).addEventListener('change', event => { state[id] = event.target.checked; render(); });
  for (const id of ['brightness', 'tempo']) document.getElementById(id).addEventListener('input', event => {
    const input = event.target, value = Number(input.value); state[id] = id === 'brightness' ? value / 100 : value;
    input.style.setProperty('--value', `${(value - Number(input.min)) / (Number(input.max) - Number(input.min)) * 100}%`);
    document.getElementById(`${id}-value`).textContent = id === 'brightness' ? `${value}%` : `${value} BPM`;
    document.querySelector('#bpm-label').textContent = `${state.tempo} BPM`; render();
  });
  function updatePause() {
    const button = document.querySelector('#pause'), label = state.paused ? 'Tiếp tục chuyển động' : 'Tạm dừng chuyển động';
    button.textContent = state.paused ? '▶' : 'Ⅱ'; button.setAttribute('aria-label', label); button.title = label;
  }
  document.querySelector('#pause').addEventListener('click', () => { state.paused = !state.paused; updatePause(); });
  let dropTimer;
  document.querySelector('#drop').addEventListener('click', () => {
    state.drop = 1; render();
    const button = document.querySelector('#drop'); button.disabled = true;
    document.querySelector('#status').textContent = state.paused ? 'DROP đang xem ở trạng thái tĩnh. Nhấn ▶ để chạy.' : 'Feel the drop. Ánh sáng phủ đầy sân khấu.';
    clearTimeout(dropTimer); dropTimer = setTimeout(() => { state.drop = 0; button.disabled = false; document.querySelector('#status').textContent = 'Sẵn sàng cho nhịp tiếp theo.'; render(); }, 2600);
  });
  document.querySelector('#fullscreen').addEventListener('click', async () => {
    try { if (document.fullscreenElement) await document.exitFullscreen(); else await document.querySelector('.preview').requestFullscreen(); }
    catch { document.querySelector('#status').textContent = 'Trình duyệt chưa hỗ trợ toàn màn hình. Bạn có thể dùng F11.'; }
  });
  const motion = matchMedia('(prefers-reduced-motion: reduce)');
  motion.addEventListener('change', event => { state.paused = event.matches; updatePause(); });
  document.addEventListener('visibilitychange', () => { last = 0; });
  new ResizeObserver(resize).observe(canvas);
  updatePause(); requestAnimationFrame(frame);
})();
