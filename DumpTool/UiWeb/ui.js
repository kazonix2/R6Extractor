(() => {
  const resources = globalThis.siegeUi.resources;
  const byUid = new Map(resources.map(resource => [resource.uid, resource]));

  function findImage(uid, visited = new Set()) {
    const key = uid.replace(/^0x/i, "").toUpperCase().padStart(16, "0");
    const resource = byUid.get(key);
    if (!resource || visited.has(key)) return undefined;
    if (resource.format === "PNG") return resource;
    visited.add(key);
    for (const reference of resource.references) {
      const image = findImage(reference, visited);
      if (image) return image;
    }
  }

  // Reuse this helper on an authored screen with <img data-ui-image="asset UID">.
  globalThis.SiegeUi = { resource: uid => byUid.get(uid), findImage };
  for (const image of document.querySelectorAll("img[data-ui-image]")) {
    const asset = findImage(image.dataset.uiImage);
    if (asset) image.src = asset.file;
  }

  const grid = document.querySelector("#resources");
  if (!grid) return;
  const query = document.querySelector("#query");
  const type = document.querySelector("#type");
  const results = document.querySelector("#results");
  const more = document.querySelector("#more");
  const pageSize = 72;
  let matches = [];
  let shown = 0;
  for (const name of [...new Set(resources.map(resource => resource.type))].sort()) {
    type.add(new Option(name, name));
  }

  function card(resource) {
    const article = document.createElement("article");
    const asset = findImage(resource.uid);
    if (asset) {
      const image = document.createElement("img");
      image.src = asset.file;
      image.alt = resource.name || resource.uid;
      image.loading = "lazy";
      image.width = asset.width;
      image.height = asset.height;
      article.append(image);
    }
    const title = document.createElement("h2");
    title.textContent = resource.name || resource.uid;
    const meta = document.createElement("p");
    meta.textContent = asset
      ? `${resource.type} · ${asset.width} × ${asset.height}`
      : `${resource.type} · ${resource.format}`;
    const download = document.createElement("a");
    download.href = resource.file;
    download.download = "";
    download.textContent = `Download ${resource.format}`;
    article.append(title, meta, download);
    if (resource.screen) {
      const open = document.createElement("a");
      open.href = resource.screen;
      open.textContent = "Open UI clip";
      const row = document.createElement("p");
      row.append(open);
      article.append(row);
    }
    if (resource.format === "TrueType" || resource.format === "OpenType") {
      const sample = document.createElement("p");
      sample.textContent = "Aa Bb Cc 0123456789";
      sample.style.fontFamily = `Siege-${resource.uid}, sans-serif`;
      article.append(sample);
    }
    if (resource.references.length) {
      const refs = document.createElement("ul");
      for (const uid of resource.references) {
        const item = document.createElement("li");
        const linked = byUid.get(uid);
        if (linked) {
          const link = document.createElement("a");
          link.href = `#${uid}`;
          link.textContent = linked.name || uid;
          item.append(link);
        } else {
          item.textContent = `${uid} (unresolved)`;
        }
        refs.append(item);
      }
      article.append(refs);
    }
    return article;
  }

  function showPage() {
    const page = matches.slice(shown, shown + pageSize);
    grid.append(...page.map(card));
    shown += page.length;
    results.textContent = `${shown.toLocaleString()} of ${matches.length.toLocaleString()} resources`;
    more.hidden = shown >= matches.length;
  }

  function filter() {
    const text = query.value.trim().toLowerCase().replace(/^0x/, "");
    matches = resources.filter(resource => (!type.value || resource.type === type.value) &&
      `${resource.uid} ${resource.name} ${resource.type} ${resource.format}`.toLowerCase().includes(text));
    grid.replaceChildren();
    shown = 0;
    showPage();
  }

  document.querySelector("#filters").addEventListener("submit", event => event.preventDefault());
  query.addEventListener("input", filter);
  type.addEventListener("change", filter);
  more.addEventListener("click", showPage);
  function followReference() {
    if (location.hash) {
      query.value = location.hash.slice(1);
      type.value = "";
    }
    filter();
  }
  window.addEventListener("hashchange", followReference);
  followReference();
})();
