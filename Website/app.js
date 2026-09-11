// Rendered by vrchat-community/package-list-action with Scriban, like index.html.
const LISTING_URL = "{{ listingInfo.Url }}";

(function () {
  "use strict";
  var root = document.documentElement;
  var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  function isDark() {
    var theme = root.getAttribute("data-theme");
    return theme ? theme === "dark" : window.matchMedia("(prefers-color-scheme: dark)").matches;
  }

  /* ---- theme toggle (the saved choice is applied by an inline script in <head>) ---- */
  var themeBtn = document.getElementById("themeBtn");
  if (themeBtn) themeBtn.addEventListener("click", function () {
    var next = isDark() ? "light" : "dark";
    root.setAttribute("data-theme", next);
    try { localStorage.setItem("fgt-theme", next); } catch (e) { /* storage blocked */ }
  });

  /* ---- scroll reveal ---- */
  var reveals = document.querySelectorAll(".rv");
  if (reduceMotion || !("IntersectionObserver" in window)) {
    reveals.forEach(function (el) { el.classList.add("in"); });
  } else {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) { entry.target.classList.add("in"); io.unobserve(entry.target); }
      });
    }, { rootMargin: "0px 0px -8% 0px", threshold: 0.08 });
    reveals.forEach(function (el) { io.observe(el); });
  }

  /* ---- copy listing URL ---- */
  function legacyCopy(text) {
    var ta = document.createElement("textarea");
    ta.value = text;
    ta.setAttribute("readonly", "");
    ta.style.position = "fixed";
    ta.style.opacity = "0";
    document.body.appendChild(ta);
    ta.select();
    var ok = false;
    try { ok = document.execCommand("copy"); } catch (e) { ok = false; }
    document.body.removeChild(ta);
    return ok;
  }

  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text).catch(function () {
        if (!legacyCopy(text)) throw new Error("copy failed");
      });
    }
    return new Promise(function (resolve, reject) {
      if (legacyCopy(text)) resolve(); else reject(new Error("copy failed"));
    });
  }

  document.querySelectorAll("[data-copy]").forEach(function (btn) {
    var label = btn.textContent;
    var timer = null;
    btn.addEventListener("click", function () {
      copyText(LISTING_URL).then(function () {
        btn.textContent = "Copied";
        btn.classList.add("ok");
      }, function () {
        var field = document.getElementById(btn.getAttribute("data-copy"));
        if (field) { field.focus(); field.select(); }
        btn.textContent = "Press Ctrl+C";
      }).then(function () {
        clearTimeout(timer);
        timer = setTimeout(function () { btn.textContent = label; btn.classList.remove("ok"); }, 1800);
      });
    });
  });

  var listingField = document.getElementById("listingUrl");
  if (listingField) listingField.addEventListener("focus", function () { listingField.select(); });

  /* ---- "Add to VCC" fallback hint: if the vcc:// link didn't pull focus away, the
         Creator Companion probably isn't installed, so offer the manual route ---- */
  var hint = document.getElementById("vccHint");
  var hintClose = document.getElementById("vccHintClose");
  document.querySelectorAll("[data-vcc]").forEach(function (link) {
    link.addEventListener("click", function () {
      if (!hint) return;
      var left = false;
      function onBlur() { left = true; }
      window.addEventListener("blur", onBlur);
      setTimeout(function () {
        window.removeEventListener("blur", onBlur);
        if (!left && !document.hidden) hint.hidden = false;
      }, 2000);
    });
  });
  if (hintClose) hintClose.addEventListener("click", function () { hint.hidden = true; });

  /* ---- screenshots: lightbox, and a placeholder if a hosted image goes missing ---- */
  var lb = document.getElementById("lb");
  var lbImg = document.getElementById("lbImg");
  var lbClose = document.getElementById("lbClose");
  var lastFocus = null;

  function openLightbox(src, alt) {
    lastFocus = document.activeElement;
    lbImg.src = src;
    lbImg.alt = alt || "";
    lb.classList.add("open");
    lbClose.focus();
  }
  function closeLightbox() {
    lb.classList.remove("open");
    lbImg.src = "";
    if (lastFocus && lastFocus.focus) lastFocus.focus();
  }

  document.querySelectorAll(".shot-btn").forEach(function (btn) {
    var img = btn.querySelector("img");
    function markBroken() { btn.classList.add("broken"); btn.disabled = true; }
    if (img) {
      img.addEventListener("error", markBroken);
      if (img.complete && img.naturalWidth === 0 && img.getAttribute("src")) markBroken();
    }
    btn.addEventListener("click", function () {
      if (btn.classList.contains("broken")) return;
      openLightbox(btn.getAttribute("data-full") || (img ? img.currentSrc || img.src : ""), img ? img.alt : "");
    });
  });
  if (lbClose) lbClose.addEventListener("click", closeLightbox);
  if (lb) lb.addEventListener("click", function (e) { if (e.target === lb) closeLightbox(); });
  document.addEventListener("keydown", function (e) {
    if (e.key !== "Escape") return;
    if (lb && lb.classList.contains("open")) closeLightbox();
    else if (hint && !hint.hidden) hint.hidden = true;
  });

  /* ---- hero fur-flow field ---- */
  var canvas = document.getElementById("furfield");
  if (canvas && canvas.getContext) {
    var ctx = canvas.getContext("2d");
    var width = 0, height = 0, t = 0, raf = null;

    function resize() {
      var dpr = Math.min(window.devicePixelRatio || 1, 2);
      var rect = canvas.getBoundingClientRect();
      width = Math.max(1, rect.width);
      height = Math.max(1, rect.height);
      canvas.width = width * dpr;
      canvas.height = height * dpr;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    function draw() {
      ctx.clearRect(0, 0, width, height);
      ctx.strokeStyle = isDark() ? "rgba(255,180,84,0.28)" : "rgba(91,75,214,0.20)";
      ctx.lineWidth = 1.4;
      var gap = 46, len = 17, head = 5;
      for (var x = gap * 0.6; x < width; x += gap) {
        for (var y = gap * 0.6; y < height; y += gap) {
          var a = 0.35 + 0.75 * Math.sin(x * 0.010 + y * 0.008 + t);
          var ex = x + Math.cos(a) * len, ey = y + Math.sin(a) * len;
          ctx.beginPath();
          ctx.moveTo(x, y); ctx.lineTo(ex, ey);
          ctx.moveTo(ex, ey); ctx.lineTo(ex - Math.cos(a - 0.5) * head, ey - Math.sin(a - 0.5) * head);
          ctx.moveTo(ex, ey); ctx.lineTo(ex - Math.cos(a + 0.5) * head, ey - Math.sin(a + 0.5) * head);
          ctx.stroke();
        }
      }
    }

    function loop() { t += 0.006; draw(); raf = requestAnimationFrame(loop); }

    window.addEventListener("resize", function () { resize(); draw(); });
    resize();
    if (reduceMotion) draw(); else loop();
    document.addEventListener("visibilitychange", function () {
      if (document.hidden) { if (raf) { cancelAnimationFrame(raf); raf = null; } }
      else if (!reduceMotion && !raf) loop();
    });
  }
})();
