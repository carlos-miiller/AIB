// Diagnóstico: o que está "cobrindo" os elementos com texto que estão dentro da tela. Devolve só
// ESTRUTURA (tag, id, classe, estilo, tamanho) — nenhum texto da página — para poder ser colado.
() => {
  const vw = innerWidth, vh = innerHeight;
  const assinatura = el => {
    if (!el) return '(nada)';
    const cs = getComputedStyle(el);
    const r = el.getBoundingClientRect();
    const cls = (typeof el.className === 'string' ? el.className : '').trim().split(/\s+/).slice(0, 4).join('.');
    return `${el.tagName.toLowerCase()}${el.id ? '#' + el.id : ''}${cls ? '.' + cls : ''}`
      + ` [${Math.round(r.left)},${Math.round(r.top)} ${Math.round(r.width)}x${Math.round(r.height)}]`
      + ` pos=${cs.position} z=${cs.zIndex} bg=${cs.backgroundColor} op=${cs.opacity} pe=${cs.pointerEvents}`;
  };

  const grupos = new Map();
  let comTexto = 0, cobertos = 0;
  for (const el of document.querySelectorAll('body *')) {
    let direto = '';
    for (const c of el.childNodes) if (c.nodeType === 3) direto += c.textContent;
    if (!direto.trim()) continue;
    const cs = getComputedStyle(el);
    if (cs.display === 'none' || cs.visibility === 'hidden') continue;
    const r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2 || r.bottom <= 0 || r.right <= 0 || r.top >= vh || r.left >= vw) continue;
    comTexto++;
    const x = (Math.max(r.left, 0) + Math.min(r.right, vw)) / 2;
    const y = (Math.max(r.top, 0) + Math.min(r.bottom, vh)) / 2;
    const h = document.elementFromPoint(x, y);
    if (h && (h === el || el.contains(h) || h.contains(el))) continue;
    cobertos++;
    // Sobe do elemento atingido até o maior ancestral que ainda não contém o coberto: é a "capa".
    let capa = h;
    while (capa && capa.parentElement && !capa.parentElement.contains(el)) capa = capa.parentElement;
    const k = assinatura(capa) + '  (atingido: ' + assinatura(h).split(' [')[0] + ')';
    grupos.set(k, (grupos.get(k) || 0) + 1);
  }

  const quadros = [...document.querySelectorAll('iframe')].map(f => {
    const r = f.getBoundingClientRect();
    const l = Math.max(r.left, 0), t = Math.max(r.top, 0);
    const w = Math.min(r.right, vw) - l, hh = Math.min(r.bottom, vh) - t;
    const pontos = w > 0 && hh > 0
      ? [[.5,.5],[.25,.25],[.75,.25],[.25,.75],[.75,.75]].map(([fx, fy]) => {
          const p = document.elementFromPoint(l + w * fx, t + hh * fy);
          return p === f ? 'ele' : assinatura(p).split(' [')[0];
        })
      : ['fora da tela'];
    let origem = '';
    try { const u = new URL(f.src, location.href); origem = u.host + u.pathname; } catch { }
    return `${assinatura(f)} src=${origem}\n      pontos: ${pontos.join(' | ')}`;
  });

  return { janela: `${vw}x${vh}`, comTexto, cobertos,
    capas: [...grupos.entries()].sort((a, b) => b[1] - a[1]).slice(0, 8).map(([k, n]) => `${n}x ${k}`),
    quadros };
}
