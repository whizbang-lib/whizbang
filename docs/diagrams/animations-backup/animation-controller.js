/**
 * Whizbang Animation Controller
 *
 * Step-by-step playback engine for event processing flow animations.
 * Provides play/pause, step forward/back, speed control, timeline, and narration.
 *
 * Usage:
 *   const ctrl = new AnimationController(steps, { container: document.body });
 *   ctrl.play();
 *
 * Each step: { id, label, narration, duration?, onEnter, onExit? }
 */
class AnimationController {
  /** @param {Step[]} steps  @param {{ container: HTMLElement }} options */
  constructor(steps, options = {}) {
    this.steps = steps;
    this.container = options.container || document.body;
    this.currentIndex = -1;
    this.playing = false;
    this.speed = 1;
    this._timer = null;
    this._onStepChange = [];

    this._buildControlBar();
    this._bindKeys();
  }

  // ── Public API ──

  play() {
    this.playing = true;
    this._updatePlayBtn();
    if (this.currentIndex < 0) this.goToStep(0);
    else this._scheduleNext();
  }

  pause() {
    this.playing = false;
    clearTimeout(this._timer);
    this._updatePlayBtn();
  }

  togglePlay() {
    this.playing ? this.pause() : this.play();
  }

  stepForward() {
    this.pause();
    if (this.currentIndex < this.steps.length - 1) {
      this.goToStep(this.currentIndex + 1);
    }
  }

  stepBackward() {
    this.pause();
    if (this.currentIndex > 0) {
      this.goToStep(this.currentIndex - 1);
    }
  }

  goToStep(index) {
    if (index < 0 || index >= this.steps.length) return;

    // Reset all steps then replay up to target
    if (index < this.currentIndex) {
      this._resetAll();
      for (let i = 0; i <= index; i++) {
        this.steps[i].onEnter(i === index);
      }
    } else if (index > this.currentIndex) {
      for (let i = this.currentIndex + 1; i <= index; i++) {
        this.steps[i].onEnter(i === index);
      }
    }

    this.currentIndex = index;
    this._updateUI();
    this._fireStepChange();

    if (this.playing) this._scheduleNext();
  }

  setSpeed(multiplier) {
    this.speed = multiplier;
    if (this.playing) {
      clearTimeout(this._timer);
      this._scheduleNext();
    }
  }

  reset() {
    this.pause();
    this._resetAll();
    this.currentIndex = -1;
    this._updateUI();
  }

  onStepChange(cb) {
    this._onStepChange.push(cb);
  }

  getCurrentStep() {
    return this.currentIndex >= 0 ? this.steps[this.currentIndex] : null;
  }

  // ── Private ──

  _resetAll() {
    for (const step of this.steps) {
      if (step.onExit) step.onExit();
    }
  }

  _scheduleNext() {
    clearTimeout(this._timer);
    if (this.currentIndex >= this.steps.length - 1) {
      this.pause();
      return;
    }
    const step = this.steps[this.currentIndex];
    const duration = (step.duration || 2000) / this.speed;
    this._timer = setTimeout(() => {
      if (this.playing) this.goToStep(this.currentIndex + 1);
    }, duration);
  }

  _fireStepChange() {
    const step = this.getCurrentStep();
    for (const cb of this._onStepChange) cb(step, this.currentIndex);
  }

  _updateUI() {
    this._updateTimeline();
    this._updateNarration();
    this._updateStepCounter();
    this._updatePlayBtn();
  }

  _updatePlayBtn() {
    if (this._playBtn) {
      this._playBtn.textContent = this.playing ? '\u23F8' : '\u25B6';
      this._playBtn.title = this.playing ? 'Pause (Space)' : 'Play (Space)';
    }
  }

  _updateTimeline() {
    const items = this._timelineEl?.children;
    if (!items) return;
    for (let i = 0; i < items.length; i++) {
      items[i].classList.toggle('active', i === this.currentIndex);
      items[i].classList.toggle('completed', i < this.currentIndex);
    }
  }

  _updateNarration() {
    if (!this._narrationEl) return;
    const step = this.getCurrentStep();
    if (!step) {
      this._narrationEl.innerHTML = '<span style="color:var(--text-muted)">Press Play or Step Forward to begin</span>';
      return;
    }
    this._narrationEl.innerHTML =
      `<span class="step-title">Step ${this.currentIndex + 1}:</span> ${step.narration || step.label}`;
  }

  _updateStepCounter() {
    if (this._counterEl) {
      this._counterEl.textContent = `${Math.max(0, this.currentIndex + 1)} / ${this.steps.length}`;
    }
  }

  // ── Build DOM ──

  _buildControlBar() {
    const bar = document.createElement('div');
    bar.className = 'control-bar';

    // Timeline
    const timeline = document.createElement('div');
    timeline.className = 'timeline';
    for (let i = 0; i < this.steps.length; i++) {
      const seg = document.createElement('div');
      seg.className = 'timeline-step';
      seg.textContent = this.steps[i].label;
      seg.title = this.steps[i].label;
      seg.addEventListener('click', () => {
        this.pause();
        this.goToStep(i);
      });
      timeline.appendChild(seg);
    }
    this._timelineEl = timeline;
    bar.appendChild(timeline);

    // Controls
    const controls = document.createElement('div');
    controls.className = 'controls-row';

    const resetBtn = this._makeBtn('\u23EE', 'Reset (Home)', () => this.reset());
    const backBtn = this._makeBtn('\u23EA', 'Step Back (\u2190)', () => this.stepBackward());
    const playBtn = this._makeBtn('\u25B6', 'Play (Space)', () => this.togglePlay());
    playBtn.classList.add('primary');
    this._playBtn = playBtn;
    const fwdBtn = this._makeBtn('\u23E9', 'Step Forward (\u2192)', () => this.stepForward());

    const counter = document.createElement('span');
    counter.className = 'step-counter';
    counter.textContent = `0 / ${this.steps.length}`;
    this._counterEl = counter;

    const speedSel = document.createElement('select');
    speedSel.className = 'speed-select';
    for (const s of [0.5, 1, 1.5, 2, 4]) {
      const opt = document.createElement('option');
      opt.value = s;
      opt.textContent = `${s}x`;
      if (s === 1) opt.selected = true;
      speedSel.appendChild(opt);
    }
    speedSel.addEventListener('change', () => this.setSpeed(Number(speedSel.value)));

    controls.append(resetBtn, backBtn, playBtn, fwdBtn, counter, speedSel);
    bar.appendChild(controls);

    // Narration
    const narration = document.createElement('div');
    narration.className = 'narration';
    narration.innerHTML = '<span style="color:var(--text-muted)">Press Play or Step Forward to begin</span>';
    this._narrationEl = narration;
    bar.appendChild(narration);

    this.container.appendChild(bar);
  }

  _makeBtn(text, title, onClick) {
    const btn = document.createElement('button');
    btn.className = 'ctrl-btn';
    btn.textContent = text;
    btn.title = title;
    btn.addEventListener('click', onClick);
    return btn;
  }

  _bindKeys() {
    document.addEventListener('keydown', (e) => {
      if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
      switch (e.key) {
        case ' ':
          e.preventDefault();
          this.togglePlay();
          break;
        case 'ArrowRight':
          e.preventDefault();
          this.stepForward();
          break;
        case 'ArrowLeft':
          e.preventDefault();
          this.stepBackward();
          break;
        case 'Home':
          e.preventDefault();
          this.reset();
          break;
      }
    });
  }
}

// ── Utility: animate a packet from one element to another ──

function animatePacket(packetEl, fromEl, toEl, duration = 800) {
  const fromRect = fromEl.getBoundingClientRect();
  const toRect = toEl.getBoundingClientRect();
  const viewportRect = fromEl.closest('.animation-viewport')?.getBoundingClientRect() || { left: 0, top: 0 };

  const startX = fromRect.left + fromRect.width / 2 - viewportRect.left;
  const startY = fromRect.top + fromRect.height / 2 - viewportRect.top;
  const endX = toRect.left + toRect.width / 2 - viewportRect.left;
  const endY = toRect.top + toRect.height / 2 - viewportRect.top;

  packetEl.style.left = startX + 'px';
  packetEl.style.top = startY + 'px';
  packetEl.classList.add('visible');

  const anim = packetEl.animate([
    { transform: 'translate(-50%, -50%)', left: startX + 'px', top: startY + 'px' },
    { transform: 'translate(-50%, -50%)', left: endX + 'px', top: endY + 'px' }
  ], { duration, easing: 'ease-in-out', fill: 'forwards' });

  return anim.finished.then(() => {
    packetEl.style.left = endX + 'px';
    packetEl.style.top = endY + 'px';
  });
}

// ── Utility: draw SVG line between two elements ──

function createConnection(svgEl, fromEl, toEl, id) {
  const viewportRect = svgEl.closest('.animation-viewport')?.getBoundingClientRect()
    || svgEl.parentElement.getBoundingClientRect();
  const fromRect = fromEl.getBoundingClientRect();
  const toRect = toEl.getBoundingClientRect();

  const x1 = fromRect.left + fromRect.width / 2 - viewportRect.left;
  const y1 = fromRect.top + fromRect.height / 2 - viewportRect.top;
  const x2 = toRect.left + toRect.width / 2 - viewportRect.left;
  const y2 = toRect.top + toRect.height / 2 - viewportRect.top;

  const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
  line.setAttribute('x1', x1);
  line.setAttribute('y1', y1);
  line.setAttribute('x2', x2);
  line.setAttribute('y2', y2);
  if (id) line.id = id;

  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.classList.add('connection');
  if (id) g.id = id + '-group';
  g.appendChild(line);
  svgEl.appendChild(g);
  return g;
}

// ── Utility: add/remove classes in bulk ──

function setClasses(el, classMap) {
  for (const [cls, on] of Object.entries(classMap)) {
    el.classList.toggle(cls, on);
  }
}

function clearAllStates(viewport) {
  viewport.querySelectorAll('.node').forEach(n => {
    n.classList.remove('glow', 'active', 'dimmed', 'highlight-success', 'highlight-warning', 'pulse');
  });
  viewport.querySelectorAll('.message-packet').forEach(p => {
    p.classList.remove('visible');
    p.getAnimations().forEach(a => a.cancel());
  });
  viewport.querySelectorAll('.detail-card').forEach(c => c.classList.remove('visible'));
  viewport.querySelectorAll('.detail-card .field').forEach(f => f.classList.remove('visible'));
  viewport.querySelectorAll('.connection').forEach(c => c.classList.remove('active'));
  viewport.querySelectorAll('.phase-region').forEach(r => {
    r.classList.remove('active', 'completed');
  });
  viewport.querySelectorAll('.lifecycle-badge').forEach(b => b.classList.remove('visible'));
  viewport.querySelectorAll('.transaction-boundary').forEach(t => t.classList.remove('visible'));
  viewport.querySelectorAll('.service-boundary').forEach(s => s.classList.remove('glow'));
  viewport.querySelectorAll('.envelope-inspector .hop-entry').forEach(h => h.classList.remove('visible'));
}
