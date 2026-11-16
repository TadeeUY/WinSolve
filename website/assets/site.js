// WinSolve website: language choice, mobile menu and the latest release from GitHub (no libraries).
(() => {
  const REPO = "TadeeUY/WinSolve";
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

  // ── Mobile menu ──
  const nav = $(".nav");
  const menuBtn = $(".menu-btn");
  if (menuBtn && nav) {
    const setOpen = (o) => { nav.classList.toggle("open", o); menuBtn.setAttribute("aria-expanded", String(o)); };
    menuBtn.addEventListener("click", () => setOpen(!nav.classList.contains("open")));
    $$(".nav .links a").forEach((a) => a.addEventListener("click", () => setOpen(false)));
    addEventListener("keydown", (e) => { if (e.key === "Escape") setOpen(false); });
  }

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
