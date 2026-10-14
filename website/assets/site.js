// WinSolve website: animations and the latest release from GitHub (no libraries).
(() => {
  const REPO = "TadeeUY/WinSolve";
  const reduce = matchMedia("(prefers-reduced-motion: reduce)").matches;
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];

  // ── Language: the visitor's choice wins; otherwise the browser's language picks ES or EN ──
  const lang = document.documentElement.lang || "es";
  const store = {
    get() { try { return localStorage.getItem("winsolve-lang"); } catch { return null; } },
    set(v) { try { localStorage.setItem("winsolve-lang", v); } catch { /* private mode: just follow the link */ } },
  };
  $$(".lang a[data-lang]").forEach((a) => a.addEventListener("click", () => store.set(a.dataset.lang)));
  const alt = $('link[rel="alternate"][hreflang]:not([hreflang="' + lang + '"])');
  // Only the Spanish pages (the default address) redirect: an English link someone shared stays English.
  if (alt && lang === "es" && !store.get()) {
    const wants = (navigator.languages || [navigator.language || ""]).some((l) => /^es\b/i.test(l)) ? "es" : "en";
    if (wants !== lang && alt.hreflang === wants) { location.replace(alt.href + location.hash); return; }
  }

  // ── Nav state + scroll progress ──
  const nav = $(".nav");
  const bar = $(".progress");
  const onScroll = () => {
    const y = scrollY;
    nav?.classList.toggle("scrolled", y > 8);
    if (bar) {
      const max = document.documentElement.scrollHeight - innerHeight;
      bar.style.transform = `scaleX(${max > 0 ? y / max : 0})`;
    }
  };
  addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  // ── Hero title: letters rise one after another ──
  $$(".split").forEach((el) => {
    let i = 0;
    const walk = (node) => {
      [...node.childNodes].forEach((n) => {
        if (n.nodeType === 3) {
          const frag = document.createDocumentFragment();
          n.textContent.split(/(\s+)/).forEach((word) => {
            if (!word) return;
            if (/^\s+$/.test(word)) { frag.append(word); return; }
            const w = document.createElement("span");
            w.style.display = "inline-block";
            w.style.whiteSpace = "nowrap";
            [...word].forEach((c) => {
              const s = document.createElement("span");
              s.className = "ch";
              s.textContent = c;
              s.style.animationDelay = `${0.15 + i++ * 0.028}s`;
              w.append(s);
            });
            frag.append(w);
          });
          n.replaceWith(frag);
        } else if (n.nodeType === 1 && n.classList.contains("grad")) {
          // Gradient text rises as one piece (per-letter layers would break the gradient clip).
          n.classList.add("ch");
          n.style.animationDelay = `${0.15 + i * 0.028}s`;
          i += n.textContent.length;
        } else if (n.nodeType === 1 && !n.classList.contains("rotator")) walk(n);
      });
    };
    walk(el);
  });

  // ── Rotating word ──
  $$(".rotator").forEach((r) => {
    const items = $$("span", r);
    if (items.length < 2) return;
    let i = 0;
    items[0].classList.add("on");
    setInterval(() => {
      items[i].classList.replace("on", "off");
      const prev = items[i];
      setTimeout(() => prev.classList.remove("off"), 700);
      i = (i + 1) % items.length;
      items[i].classList.add("on");
    }, reduce ? 4000 : 2400);
  });

  // ── Reveal on scroll (with stagger inside groups) ──
  const io = new IntersectionObserver((entries) => {
    entries.forEach((e) => {
      if (!e.isIntersecting) return;
      e.target.classList.add("in");
      io.unobserve(e.target);
    });
  }, { threshold: 0.15, rootMargin: "0px 0px -40px 0px" });
  $$("[data-stagger]").forEach((g) => $$(".reveal", g).forEach((el, i) => el.style.setProperty("--d", `${i * 0.08}s`)));
  $$(".reveal").forEach((el) => io.observe(el));

  // ── Spotlight that follows the pointer on cards and the primary button ──
  $$(".card").forEach((c) => c.addEventListener("pointermove", (e) => {
    const r = c.getBoundingClientRect();
    c.style.setProperty("--mx", `${e.clientX - r.left}px`);
    c.style.setProperty("--my", `${e.clientY - r.top}px`);
  }));
  $$(".btn.primary").forEach((b) => {
    b.addEventListener("pointermove", (e) => {
      const r = b.getBoundingClientRect();
      b.style.setProperty("--x", `${e.clientX - r.left}px`);
      b.style.setProperty("--y", `${e.clientY - r.top}px`);
      if (!reduce && b.classList.contains("magnetic")) {
        const dx = (e.clientX - r.left - r.width / 2) / r.width;
        const dy = (e.clientY - r.top - r.height / 2) / r.height;
        b.style.transform = `translate(${dx * 8}px, ${dy * 8}px)`;
      }
    });
    b.addEventListener("pointerleave", () => (b.style.transform = ""));
  });

  // ── App mockup: tilts flat while scrolling, then "scans" ──
  const win = $(".window");
  if (win) {
    const tilt = () => {
      const r = win.getBoundingClientRect();
      const p = Math.min(1, Math.max(0, 1 - (r.top - innerHeight * 0.15) / (innerHeight * 0.75)));
      win.style.transform = reduce ? "none" : `rotateX(${16 * (1 - p)}deg) scale(${0.94 + 0.06 * p})`;
    };
    addEventListener("scroll", tilt, { passive: true });
    tilt();
    let ran = false;
    new IntersectionObserver(([e], o) => {
      if (!e.isIntersecting || ran) return;
      ran = true;
      o.disconnect();
      const val = $(".ring .val", win);
      const num = $(".ring .num b", win);
      const score = 94;
      setTimeout(() => (val.style.strokeDashoffset = String(380 * (1 - score / 100))), 300);
      countTo(num, score, 2000);
      $$(".check", win).forEach((c, i) => setTimeout(() => c.classList.add("done"), 500 + i * 420));
      $$(".tile .bar i", win).forEach((b) => setTimeout(() => (b.style.width = b.dataset.w), 600));
    }, { threshold: 0.35 }).observe(win);
  }

  // ── Questions: smooth open/close (height, content and icon), one open at a time per list ──
  $$("details").forEach((d) => {
    const summary = $("summary", d);
    if (!summary) return;
    const body = document.createElement("div");
    body.className = "acc";
    while (summary.nextSibling) body.append(summary.nextSibling);
    d.append(body);
    if (d.open) d.classList.add("is-open");
    let anim = null;
    const ease = "cubic-bezier(.2, .8, .2, 1)";
    const toggle = (open) => {
      if (open === d.classList.contains("is-open")) return;
      anim?.cancel();
      const from = d.offsetHeight;
      d.classList.toggle("is-open", open);
      if (reduce) { d.open = open; return; }
      if (open) d.open = true;
      const closed = summary.offsetHeight + (d.offsetHeight - d.clientHeight);
      const to = open ? d.offsetHeight : closed;
      anim = d.animate({ height: [`${from}px`, `${to}px`] }, { duration: open ? 460 : 340, easing: ease });
      if (open) body.animate({ opacity: [0, 1], transform: ["translateY(-8px)", "none"], filter: ["blur(4px)", "none"] }, { duration: 420, delay: 60, easing: ease, fill: "backwards" });
      else body.animate({ opacity: [1, 0], transform: ["none", "translateY(-6px)"] }, { duration: 220, easing: "ease-in" });
      anim.onfinish = () => { anim = null; if (!open) d.open = false; };
    };
    summary.addEventListener("click", (e) => {
      e.preventDefault();
      const open = !d.classList.contains("is-open");
      if (open) $$("details.is-open", d.parentElement).forEach((o) => o !== d && o.dispatchEvent(new CustomEvent("acc-close")));
      toggle(open);
    });
    d.addEventListener("acc-close", () => toggle(false));
  });

  // ── Counters ──
  function countTo(el, to, ms = 1600) {
    if (!el) return;
    const suffix = el.dataset.suffix || "";
    if (reduce) { el.textContent = to + suffix; return; }
    const t0 = performance.now();
    const step = (t) => {
      const p = Math.min(1, (t - t0) / ms);
      el.textContent = Math.round(to * (1 - Math.pow(1 - p, 3))) + suffix;
      if (p < 1) requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  }
  const cio = new IntersectionObserver((entries) => entries.forEach((e) => {
    if (!e.isIntersecting) return;
    countTo(e.target, +e.target.dataset.count);
    cio.unobserve(e.target);
  }), { threshold: 0.6 });
  $$("[data-count]").forEach((el) => cio.observe(el));

  // ── Marquee: duplicate the track so it loops seamlessly ──
  $$(".marquee").forEach((m) => {
    const t = $(".track", m);
    if (t) { const c = t.cloneNode(true); c.setAttribute("aria-hidden", "true"); m.append(c); }
  });

  // ── Latest release (version, date, sizes, checksum) ──
  const mb = (b) => `${(b / 1048576).toLocaleString(lang, { maximumFractionDigits: b > 10485760 ? 0 : 1 })} MB`;
  const date = (s) => new Date(s).toLocaleDateString(lang, { day: "numeric", month: "long", year: "numeric" });
  fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { headers: { Accept: "application/vnd.github+json" } })
    .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
    .then((rel) => {
      const v = rel.tag_name.replace(/^v/i, "");
      $$("[data-version]").forEach((el) => (el.textContent = v));
      $$("[data-date]").forEach((el) => (el.textContent = date(rel.published_at)));
      const asset = (n) => rel.assets.find((a) => a.name === n);
      const setup = asset("WinSolveSetup.exe");
      const portable = asset("WinSolve-Portable.exe");
      if (setup) $$("[data-size-setup]").forEach((el) => (el.textContent = mb(setup.size)));
      if (portable) $$("[data-size-portable]").forEach((el) => (el.textContent = mb(portable.size)));
      const sha = setup?.digest?.replace(/^sha256:/, "");
      if (sha) $$("[data-sha]").forEach((el) => (el.textContent = sha));
    })
    .catch(() => {});

  // ── Docs: highlight the section being read ──
  const links = $$(".toc a[href^='#']");
  if (links.length) {
    const map = new Map(links.map((a) => [a.getAttribute("href").slice(1), a]));
    const tio = new IntersectionObserver((entries) => entries.forEach((e) => {
      if (!e.isIntersecting) return;
      links.forEach((a) => a.classList.remove("active"));
      map.get(e.target.id)?.classList.add("active");
    }), { rootMargin: "-30% 0px -60% 0px" });
    map.forEach((_, id) => { const s = document.getElementById(id); if (s) tio.observe(s); });
  }

  // ── Docs: version history from GitHub releases ──
  const hist = $("#history-list");
  if (hist) {
    fetch(`https://api.github.com/repos/${REPO}/releases?per_page=8`)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((list) => {
        hist.innerHTML = "";
        list.filter((r) => !r.draft).forEach((r) => {
          const el = document.createElement("article");
          el.className = "release";
          const h = document.createElement("header");
          const b = document.createElement("b");
          b.textContent = r.tag_name;
          const s = document.createElement("span");
          s.textContent = date(r.published_at);
          const a = document.createElement("a");
          a.href = r.html_url;
          a.textContent = "GitHub ↗";
          a.style.marginLeft = "auto";
          a.style.color = "var(--blue)";
          h.append(b, s, a);
          el.append(h);
          hist.append(el);
        });
      })
      .catch(() => {
        hist.innerHTML = "";
        const p = document.createElement("p");
        p.innerHTML = lang === "en"
          ? 'Could not load it right now. See it on <a style="color:var(--blue)" href="https://github.com/TadeeUY/WinSolve/releases">GitHub</a>.'
          : 'No se pudo cargar ahora. Míralo en <a style="color:var(--blue)" href="https://github.com/TadeeUY/WinSolve/releases">GitHub</a>.';
        hist.append(p);
      });
  }
})();
