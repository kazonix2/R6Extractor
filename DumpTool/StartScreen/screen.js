(() => {
  "use strict";
  const { movie, images, fonts, shapes, text, isStartScreen } = globalThis.startScreen;
  const strings = globalThis.siegeLocalization.strings;
  const label = field => strings[field.localizationIndex] ?? field.text;
  const promptField = isStartScreen ? Object.values(text).find(field => field.text.startsWith("PRESS ANY ")) : null;
  const prompt = promptField ? label(promptField) : "";
  const stage = document.querySelector("#stage");
  const layers = document.querySelector("#layers");
  const defs = stage.querySelector("defs");
  const start = document.querySelector("#start");
  const status = document.querySelector("#status");
  const ns = "http://www.w3.org/2000/svg";
  const identity = [256, 256, 256, 256, 0, 0, 0, 0];
  const hiddenControls = new Set(["ps4Button", "stepper", "confirmButton"]);
  const atlasImages = new Map();
  let filterId = 0;
  let sequence = 0;
  let letters = null;
  let contentLayer = null;
  let promptBounds = null;
  let started = false;

  function svg(tag, attributes = {}) {
    const element = document.createElementNS(ns, tag);
    for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, value);
    return element;
  }

  function matrix(values) { return `matrix(${values.join(" ")})`; }

  function picture(file, width, height, attributes = {}) {
    return svg("image", { href: file, width, height, y: -height, transform: "scale(1 -1)",
      preserveAspectRatio: "none", ...attributes });
  }

  function colorFilter(id) {
    const filter = svg("filter", { id, x: 0, y: 0, width: 1, height: 1, "color-interpolation-filters": "sRGB" });
    const transfer = svg("feComponentTransfer");
    const channels = ["R", "G", "B", "A"].map(channel => svg(`feFunc${channel}`, { type: "linear" }));
    transfer.append(...channels);
    filter.append(transfer);
    defs.append(filter);
    return { filter, channels };
  }

  function paint(leaf, values) {
    const key = values.join(",");
    if (key === leaf.colorKey) return;
    leaf.colorKey = key;
    const tinted = values.slice(0, 3).some((value, i) => value !== 256 || values[i + 4] !== 0) || values[7] !== 0;
    if (tinted) {
      leaf.tint ??= colorFilter(`color-${++filterId}`);
      leaf.tint.channels.forEach((channel, i) => {
        channel.setAttribute("slope", values[i] / 256);
        channel.setAttribute("intercept", values[i + 4] / 255);
      });
      leaf.element.setAttribute("filter", `url(#${leaf.tint.filter.id})`);
      leaf.element.removeAttribute("opacity");
    } else {
      leaf.element.removeAttribute("filter");
      leaf.element.setAttribute("opacity", Math.max(0, Math.min(1, values[3] / 256)));
    }
  }

  function combine(parent, local) {
    if (!local) return parent;
    return parent.map((value, i) => i < 4 ? value * local[i] / 256 : value + local[i] * parent[i - 4] / 256);
  }

  function textElement(field) {
    const font = fonts[field.font];
    const group = svg("g");
    const isPrompt = field === promptField;
    const value = label(field);
    if (!value) return group;
    const [left, right, top, bottom] = field.bounds;
    const align = field.alignment === 2 ? "middle" : field.alignment === 1 ? "end" : "start";
    const atlas = font && atlasImages.get(font.atlas);
    if (!atlas || [...value].some(character => character !== "\n" && !font.glyphs[character])) {
      const line = svg("text", { x: align === "middle" ? (left + right) / 2 : align === "end" ? right : left,
        y: top + field.size, fill: field.color, "font-family": "system-ui", "font-size": field.size, "text-anchor": align });
      line.textContent = value;
      group.append(line);
      return group;
    }
    if (isPrompt) {
      promptBounds = svg("rect", { x: left, y: top, width: right - left, height: bottom - top, fill: "none" });
      group.append(promptBounds);
    }
    // Fire stores the atlas glyph quad at one-eighth pixel resolution.
    // Approximate its native strike with 48 pixels per em. The field size and
    // the advances come from EditText and the original font metrics.
    const scale = field.size / (48 * 8);
    const color = field.color.slice(0, 7);
    const maskId = `glyph-${color.slice(1)}`;
    if (!document.getElementById(maskId)) {
      const mask = colorFilter(maskId);
      mask.channels.slice(0, 3).forEach((channel, index) => {
        channel.setAttribute("slope", 0);
        channel.setAttribute("intercept", parseInt(color.slice(1 + index * 2, 3 + index * 2), 16) / 255);
      });
      mask.channels[3].setAttribute("slope", 4);
      mask.channels[3].setAttribute("intercept", -1.5);
    }
    for (const [lineIndex, line] of value.split(/\r?\n/).entries()) {
      const glyphs = [...line].map(character => font.glyphs[character] ?? font.glyphs[" "]);
      const width = glyphs.reduce((sum, glyph) => sum + glyph.advance * field.size / 20480, 0);
      let x = align === "middle" ? (left + right - width) / 2 : align === "end" ? right - width - 2 : left + 2;
      for (const [i, glyph] of glyphs.entries()) {
        if (line[i] !== " ") {
          const [u, v, u2, v2] = glyph.uv;
          const crop = svg("svg", {
            x: x + glyph.x * scale, y: top + 2 + lineIndex * field.size + glyph.y * scale,
            width: glyph.width * scale, height: glyph.height * scale,
            viewBox: `${u * atlas.width} ${v * atlas.height} ${(u2 - u) * atlas.width} ${(v2 - v) * atlas.height}`,
            overflow: "hidden", preserveAspectRatio: "none"
          });
          crop.append(picture(font.atlas, atlas.width, atlas.height, { filter: `url(#${maskId})` }));
          group.append(crop);
        }
        x += glyph.advance * field.size / 20480;
      }
    }
    return group;
  }

  function shapeElement(id, shape) {
    const path = svg("path", { d: shape.path, fill: shape.fill ?? "none", "fill-rule": "evenodd" });
    if (shape.bitmap === null || !images[shape.bitmap]) return path;
    const clip = svg("clipPath", { id: `shape-${id}-${++filterId}` });
    clip.append(path);
    defs.append(clip);
    const group = svg("g", { "clip-path": `url(#${clip.id})` });
    const [a, b, c, d, e, f] = shape.matrix;
    const artwork = svg("g", { transform: matrix([a / 20, b / 20, c / 20, d / 20, e, f]) });
    const symbol = movie.symbols[shape.bitmap];
    artwork.append(picture(images[shape.bitmap], symbol.width, symbol.height));
    group.append(artwork);
    return group;
  }

  function makeLeaf(id) {
    if (shapes[id]) return { element: shapeElement(id, shapes[id]) };
    if (text[id]) return { element: textElement(text[id]) };
    if (images[id]) {
      const { width, height } = movie.symbols[id];
      return { element: picture(images[id], width, height) };
    }
    return { element: svg("g", { "data-unsupported-symbol": id }) };
  }

  function remove(instance) {
    if (instance.leaf?.tint) instance.leaf.tint.filter.remove();
    if (instance.clip) for (const child of instance.clip.depths.values()) remove(child);
    instance.element.remove();
  }

  function newClip(id, element) {
    return { id, element, timeline: id === 0 ? movie.stage : movie.sprites[id], frame: -1, depths: new Map() };
  }

  function trackHover(name, element) {
    element.setAttribute("pointer-events", "bounding-box");
    element.addEventListener("pointerenter", () => {
      if (name === "button_0") sequence = sequence < 13 ? sequence + 1 : 0;
      else {
        if (sequence === 13 && letters) letters.reveal = true;
        sequence = 0;
      }
    });
  }

  function applyFrame(clip, frame) {
    let reorder = false;
    for (const change of frame.changes) {
      let instance = clip.depths.get(change.depth);
      if (change.remove) {
        if (instance) remove(instance);
        clip.depths.delete(change.depth);
        continue;
      }
      if (isStartScreen && hiddenControls.has(change.name)) continue;
      if (change.character !== null) {
        if (instance) remove(instance);
        const element = svg("g");
        const child = movie.sprites[change.character] ? newClip(change.character, element) : null;
        const leaf = child ? null : makeLeaf(change.character);
        const previous = change.move ? instance : null;
        instance = { element, clip: child, leaf, local: previous?.local ?? null };
        const previousMatrix = previous?.element.getAttribute("transform");
        if (previousMatrix) element.setAttribute("transform", previousMatrix);
        if (leaf) element.append(leaf.element);
        if (change.name) element.dataset.instance = change.name;
        if (change.name === "letters") letters = child;
        if (change.name === "contentLayer") contentLayer = instance;
        if (isStartScreen && (change.name === "button_0" || change.name === "button_1")) trackHover(change.name, element);
        clip.depths.set(change.depth, instance);
        reorder = true;
      }
      if (!instance) continue;
      if (change.matrix) instance.element.setAttribute("transform", matrix(change.matrix));
      if (change.colorTransform) instance.local = change.colorTransform;
    }
    if (reorder) {
      for (const [, instance] of [...clip.depths].sort(([left], [right]) => left - right))
        clip.element.append(instance.element);
    }
  }

  function rewind(clip) {
    const initialDepths = new Set(clip.timeline.frames[0].changes.filter(change => change.character !== null).map(change => change.depth));
    for (const [depth, instance] of clip.depths) {
      if (initialDepths.has(depth)) continue;
      remove(instance);
      clip.depths.delete(depth);
    }
  }

  function update(clip, parentColor) {
    const last = clip.timeline.frames.length - 1;
    let next = clip.frame + 1;
    // Stop frames and visibility come from the authored clip's frame scripts.
    if (last < 0) return;
    if (isStartScreen && clip.id === 387) next = Math.min(next, 299);
    if (isStartScreen && clip.id === 382) next = Math.min(next, 290);
    if (isStartScreen && clip.id === 378 && !clip.reveal) next = 0;
    if (next > last) {
      next = 0;
      if (isStartScreen && clip.id === 378) clip.reveal = false;
      if (clip.frame !== 0) rewind(clip);
    }
    if (next !== clip.frame) {
      applyFrame(clip, clip.timeline.frames[next]);
      clip.frame = next;
    }
    for (const instance of clip.depths.values()) {
      const values = combine(parentColor, instance.local);
      if (instance.clip) update(instance.clip, values);
      else paint(instance.leaf, values);
    }
  }

  function activate() {
    if (start.disabled || started) return;
    started = true;
    document.dispatchEvent(new CustomEvent("siege-start", { detail: { build: "Y2S3", screen: movie.rootClass } }));
  }

  function positionButton() {
    if (!promptBounds) return;
    const bounds = promptBounds.getBoundingClientRect();
    start.style.left = `${bounds.left}px`;
    start.style.top = `${bounds.top}px`;
    start.style.width = `${bounds.width}px`;
    start.style.height = `${bounds.height}px`;
  }

  function loadImage(path) {
    const image = new Image();
    return new Promise((resolve, reject) => {
      image.onload = () => resolve(image);
      image.onerror = () => reject(new Error(`Unable to load ${path}`));
      image.src = path;
    });
  }

  async function play() {
    document.title = movie.rootClass ?? "Rainbow Six Siege UI clip";
    stage.setAttribute("viewBox", [movie.x / 20, movie.y / 20, movie.width / 20, movie.height / 20].join(" "));
    stage.style.setProperty("--aspect", movie.width / movie.height);
    document.querySelector("#background").setAttribute("fill", movie.background);
    start.textContent = prompt;
    start.addEventListener("click", activate);
    window.addEventListener("keydown", event => {
      if (!event.repeat && !event.altKey && !event.ctrlKey && !event.metaKey && event.key !== "Tab") activate();
    });
    await Promise.all(Object.values(fonts).map(async font => atlasImages.set(font.atlas, await loadImage(font.atlas))));
    await Promise.all(Object.values(images).map(loadImage));
    new ResizeObserver(positionButton).observe(stage);
    const root = newClip(0, layers);
    const seek = new URLSearchParams(location.search).get("frame");
    let rendered = -1;
    function draw(frame) {
      if (rendered === frame) return;
      while (rendered < frame) {
        update(root, identity);
        rendered++;
      }
      start.disabled = !isStartScreen || !contentLayer?.local || contentLayer.local[3] === 0;
      if (!start.disabled) positionButton();
      stage.dataset.frame = rendered;
    }
    if (seek !== null && /^\d{1,4}$/.test(seek)) {
      draw(Number(seek));
      return;
    }
    let begin = performance.now();
    let hiddenAt = null;
    document.addEventListener("visibilitychange", () => {
      if (document.hidden) hiddenAt = performance.now();
      else if (hiddenAt !== null) {
        begin += performance.now() - hiddenAt;
        hiddenAt = null;
      }
    });
    function tick(now) {
      if (hiddenAt === null) draw(Math.floor((now - begin) * movie.framesPerSecond / 1000));
      requestAnimationFrame(tick);
    }
    requestAnimationFrame(tick);
  }

  play().catch(error => {
    status.hidden = false;
    status.textContent = error.message;
  });
})();
