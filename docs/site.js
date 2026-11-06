(function () {
  'use strict';

  var LATEST_API = 'https://api.github.com/repos/erickfuzer/Sirius/releases/latest';
  var root = document.documentElement;
  var quiet = matchMedia('(prefers-reduced-motion: reduce)').matches;
  var clamp = function (v, a, b) { return Math.min(b, Math.max(a, v)); };
  var all = function (s) { return Array.prototype.slice.call(document.querySelectorAll(s)); };

  // Tema: um círculo abre a partir do botão, onde o navegador souber fazer isso.
  var toggle = document.querySelector('[data-theme-toggle]');
  toggle.addEventListener('click', function () {
    var next = root.dataset.theme === 'claro' ? 'grafite' : 'claro';
    var apply = function () {
      root.dataset.theme = next;
      try { localStorage.setItem('sirius-tema', next); } catch (e) {}
    };
    if (!document.startViewTransition || quiet) { apply(); return; }
    // Pelo botão, e não pelo clique: quem usa o teclado também começa do lugar certo.
    var box = toggle.getBoundingClientRect();
    root.style.setProperty('--tx', (box.left + box.width / 2) + 'px');
    root.style.setProperty('--ty-pos', (box.top + box.height / 2) + 'px');
    document.startViewTransition(apply);
  });

  // Baixar: o link já aponta para a página da última versão. A consulta troca pelo próprio
  // instalador, e se ela falhar (sem internet, limite do GitHub) o link continua servindo.
  function applyRelease(release) {
    var version = String(release.tag || '').replace(/^v/, '');
    if (!version) return;
    all('[data-version-short]').forEach(function (el) { el.textContent = 'versão ' + version; });
    var size = release.size ? ', ' + Math.round(release.size / 1048576) + ' MB' : '';
    all('[data-version-full]').forEach(function (el) { el.textContent = 'Versão ' + version + ' para Windows 10 e 11, 64 bits' + size; });
    all('[data-release-notes]').forEach(function (el) { if (release.page) el.href = release.page; });
    if (release.url) all('[data-download]').forEach(function (el) { el.href = release.url; el.dataset.direct = '1'; });
  }

  function loadRelease() {
    var cached = null;
    try { cached = JSON.parse(sessionStorage.getItem('sirius-versao')); } catch (e) {}
    if (cached) { applyRelease(cached); return; }
    if (!window.fetch) return;
    fetch(LATEST_API, { headers: { Accept: 'application/vnd.github+json' } })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
      .then(function (data) {
        var asset = (data.assets || []).filter(function (a) { return /Setup\.exe$/i.test(a.name); })[0];
        var release = { tag: data.tag_name, page: data.html_url, url: asset && asset.browser_download_url, size: asset && asset.size };
        applyRelease(release);
        try { sessionStorage.setItem('sirius-versao', JSON.stringify(release)); } catch (e) {}
      })
      .catch(function () {});
  }
  loadRelease();

  // Depois de clicar em baixar, o aviso do Windows já não pega ninguém de surpresa.
  var toast = document.querySelector('.toast');
  all('[data-download]').forEach(function (el) {
    el.addEventListener('click', function () { if (el.dataset.direct) toast.hidden = false; });
  });
  toast.querySelector('.toast-close').addEventListener('click', function () { toast.hidden = true; });

  // Céu da abertura: posições fixas, para não mudar a cada visita.
  (function () {
    var sky = document.querySelector('.sky');
    if (!sky) return;
    var seed = 7;
    var rnd = function () { seed = (seed * 16807) % 2147483647; return (seed - 1) / 2147483646; };
    var html = '';
    for (var i = 0; i < 90; i++) {
      var size = rnd() < .12 ? 2.4 : 1 + rnd();
      html += '<i style="left:' + (rnd() * 100).toFixed(2) + '%;top:' + (rnd() * 100).toFixed(2) + '%;width:' + size.toFixed(1) + 'px;height:' + size.toFixed(1)
        + 'px;--d:' + (2.5 + rnd() * 4).toFixed(1) + 's;--o:' + (rnd() * -5).toFixed(1) + 's;--b:' + (.35 + rnd() * .55).toFixed(2) + '"></i>';
    }
    sky.innerHTML = html;
  })();

  // Revelar ao chegar perto
  var reveals = all('.reveal');
  if ('IntersectionObserver' in window) {
    var seen = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) { entry.target.classList.add('in'); seen.unobserve(entry.target); }
      });
    }, { rootMargin: '0px 0px -8% 0px', threshold: .06 });
    reveals.forEach(function (el) { seen.observe(el); });
  } else {
    reveals.forEach(function (el) { el.classList.add('in'); });
  }

  // Tudo o que anda com a rolagem
  var heroShot = document.querySelector('.hero-shot');
  var story = document.querySelector('.story');
  var win = document.querySelector('.win');
  var steps = all('.story-copy article');
  var capsule = document.querySelector('.capsule');
  var pageThread = document.querySelector('.page-thread');
  var download = document.getElementById('baixar');
  var ticking = false;
  var currentStep = -1;

  function setStep(step) {
    if (step === currentStep) return;
    currentStep = step;
    win.dataset.stage = step;
    steps.forEach(function (el, i) { el.classList.toggle('on', i === step); });
  }

  function frame() {
    ticking = false;
    var vh = innerHeight;

    var heroTop = heroShot.getBoundingClientRect().top;
    heroShot.style.setProperty('--h', clamp((vh * .92 - heroTop) / (vh * .75), 0, 1).toFixed(3));

    var box = story.getBoundingClientRect();
    var p = clamp(-box.top / Math.max(1, box.height - vh), 0, 1);
    // A primeira parte é o fio descendo, linha por linha, até a linha 8.
    win.style.setProperty('--line', p < .36 ? 1 + Math.round(p / .36 * 7) : 8);
    setStep(p < .36 ? 0 : p < .68 ? 1 : 2);

    var inStory = box.top < vh * .55 && box.bottom > vh * .45;
    var low = download.getBoundingClientRect().top < vh * .72;
    capsule.classList.toggle('show', !inStory && !low);

    var total = document.documentElement.scrollHeight - vh;
    pageThread.style.setProperty('--fio', (scrollY / Math.max(1, total) * vh).toFixed(1) + 'px');
    pageThread.classList.toggle('on', scrollY > vh * .4 && !inStory);
  }

  function schedule() {
    if (!ticking) { ticking = true; requestAnimationFrame(frame); }
  }
  addEventListener('scroll', schedule, { passive: true });
  addEventListener('resize', schedule);
  frame();

  // O Emmet de verdade faz exatamente isto, e o resultado é o mesmo dos testes da IDE.
  (function () {
    var card = document.querySelector('[data-emmet]');
    if (!card) return;
    var box = card.querySelector('.emmet');
    var typed = box.querySelector('.emmet-typed span');
    var text = 'div.cartao>h2+p';
    if (quiet) { box.classList.add('out'); return; }
    var wait = function (ms) { return new Promise(function (ok) { setTimeout(ok, ms); }); };
    var running = false;
    var visible = false;

    async function loop() {
      running = true;
      while (visible) {
        box.classList.remove('out', 'key', 'press');
        typed.textContent = '';
        await wait(500);
        for (var i = 1; i <= text.length && visible; i++) {
          typed.textContent = text.slice(0, i);
          await wait(70 + (i % 3) * 22);
        }
        box.classList.add('key');
        await wait(620);
        box.classList.add('press');
        await wait(130);
        box.classList.remove('press');
        box.classList.add('out');
        await wait(420);
        box.classList.remove('key');
        await wait(3200);
      }
      running = false;
    }

    if (!('IntersectionObserver' in window)) { box.classList.add('out'); return; }
    new IntersectionObserver(function (entries) {
      visible = entries[0].isIntersecting;
      if (visible && !running) loop();
    }, { threshold: .4 }).observe(box);
  })();

  // Comparar os temas: uma varrida leve na primeira vez ensina que dá para arrastar.
  (function () {
    var compare = document.querySelector('.compare');
    if (!compare) return;
    var input = compare.querySelector('input');
    var set = function (v) { compare.style.setProperty('--x', v + '%'); };
    var touched = false;
    input.addEventListener('input', function () { touched = true; set(input.value); });
    if (quiet || !('IntersectionObserver' in window)) return;
    var hint = new IntersectionObserver(function (entries) {
      if (!entries[0].isIntersecting) return;
      hint.disconnect();
      var start = performance.now();
      (function step(now) {
        if (touched) return;
        var t = (now - start) / 2600;
        if (t >= 1) { set(50); input.value = 50; return; }
        var v = 50 + Math.sin(t * Math.PI * 2) * 24 * (1 - t);
        set(v.toFixed(2));
        input.value = v;
        requestAnimationFrame(step);
      })(start);
    }, { threshold: .5 });
    hint.observe(compare);
  })();
})();
