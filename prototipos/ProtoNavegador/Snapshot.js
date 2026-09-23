// Percorre o DOM de um frame e devolve a página como lista de nós, na ordem de leitura.
// Cada nó interessante ganha data-aib-ref para ser clicado depois. Roda dentro da página.
(prefixo) => {
  const nos = [];
  let n = 0;
  const vw = window.innerWidth, vh = window.innerHeight;

  const IMPLICITO = {
    A: el => el.hasAttribute('href') ? 'link' : null,
    BUTTON: () => 'botão',
    SELECT: () => 'lista',
    TEXTAREA: () => 'caixa de texto',
    INPUT: el => {
      const t = (el.getAttribute('type') || 'text').toLowerCase();
      if (t === 'hidden') return null;
      if (['button', 'submit', 'reset', 'image'].includes(t)) return 'botão';
      if (t === 'checkbox') return 'caixa de seleção';
      if (t === 'radio') return 'opção';
      if (t === 'search') return 'busca';
      return 'caixa de texto';
    },
    H1: () => 'título', H2: () => 'título', H3: () => 'título', H4: () => 'título',
    TABLE: () => 'tabela', TR: () => 'linha', IMG: el => (el.getAttribute('alt') ? 'imagem' : null),
    NAV: () => 'navegação', MAIN: () => 'principal', HEADER: () => 'cabeçalho', FORM: () => 'formulário',
    DIALOG: () => 'diálogo',
  };
  const ARIA = {
    button: 'botão', link: 'link', textbox: 'caixa de texto', searchbox: 'busca', combobox: 'lista',
    checkbox: 'caixa de seleção', radio: 'opção', tab: 'aba', menuitem: 'item de menu', option: 'opção',
    heading: 'título', table: 'tabela', grid: 'tabela', treegrid: 'tabela', row: 'linha',
    navigation: 'navegação', main: 'principal', dialog: 'diálogo', switch: 'chave',
  };
  const INLINE = new Set(['A', 'SPAN', 'B', 'STRONG', 'I', 'EM', 'CODE', 'SMALL', 'SUP', 'SUB', 'ABBR',
    'MARK', 'BR', 'U', 'S', 'TIME', 'CITE', 'Q', 'IMG', 'WBR', 'BDI', 'KBD']);
  const INTERATIVO = new Set(['botão', 'link', 'caixa de texto', 'busca', 'lista', 'caixa de seleção',
    'opção', 'aba', 'item de menu', 'chave']);

  const papel = el => {
    const r = (el.getAttribute('role') || '').split(' ')[0];
    if (r && ARIA[r]) return ARIA[r];
    const f = IMPLICITO[el.tagName];
    return f ? f(el) : null;
  };

  const limpar = s => (s || '').replace(/\s+/g, ' ').trim();
  const curto = (s, max) => s.length > max ? s.slice(0, max - 1) + '…' : s;

  const nome = el => {
    const lb = el.getAttribute('aria-labelledby');
    if (lb) {
      const t = lb.split(' ').map(id => document.getElementById(id)).filter(Boolean).map(e => e.innerText).join(' ');
      if (limpar(t)) return limpar(t);
    }
    const a = el.getAttribute('aria-label');
    if (limpar(a)) return limpar(a);
    if (el.tagName === 'IMG') return limpar(el.getAttribute('alt'));
    if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT') {
      if (el.id) {
        const l = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
        if (l && limpar(l.innerText)) return limpar(l.innerText);
      }
      const t = limpar(el.getAttribute('placeholder')) || limpar(el.getAttribute('title'));
      if (t) return t;
      // Sem rótulo ligado: o texto curto que vem logo antes do campo ("Assignee" acima da
      // caixa). Sobe até três níveis procurando um irmão anterior com texto.
      for (let a = el, nivel = 0; a && nivel < 3; a = a.parentElement, nivel++) {
        for (let s = a.previousElementSibling; s; s = s.previousElementSibling) {
          // Irmão que já tem campo é o grupo de outro campo: o rótulo dele não é deste.
          if (s.matches('input,select,textarea') || s.querySelector('input,select,textarea')) break;
          const txt = limpar(s.innerText);
          if (txt && txt.length <= 40) return txt;
          if (txt) break;
        }
      }
      if (limpar(el.getAttribute('name'))) return limpar(el.getAttribute('name'));
    }
    return limpar(el.innerText) || limpar(el.getAttribute('title')) || limpar(el.value);
  };

  // Invisível conta como escondido: display, visibility, opacidade, tamanho zero, ou texto da
  // mesma cor do fundo não é verificado aqui (fica anotado como limite da v1).
  const visivel = el => {
    const cs = getComputedStyle(el);
    if (cs.display === 'none' || cs.visibility === 'hidden' || parseFloat(cs.opacity) === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 1 && r.height > 1;
  };
  // Na tela = dentro da janela E por cima. Sistemas como o Bitrix empilham painéis (chat, perfil,
  // tarefas); o que está coberto não é o que a pessoa vê. Confere o ponto do meio da parte
  // visível, como o clique do mouse faria.
  const naTela = el => {
    const r = el.getBoundingClientRect();
    if (!(r.bottom > 0 && r.right > 0 && r.top < vh && r.left < vw)) return false;
    const x = (Math.max(r.left, 0) + Math.min(r.right, vw)) / 2;
    const y = (Math.max(r.top, 0) + Math.min(r.bottom, vh)) / 2;
    const h = document.elementFromPoint(x, y);
    if (!h) return false;
    return h === el || el.contains(h) || h.contains(el);
  };

  const ref = el => {
    const id = prefixo + 'e' + (++n);
    el.setAttribute('data-aib-ref', id);
    return id;
  };

  const andar = (el, prof, oculto) => {
    if (!(el instanceof Element)) return;
    if (['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'SVG', 'svg'].includes(el.tagName)) return;
    const esconde = oculto || el.getAttribute('aria-hidden') === 'true' || !visivel(el);
    const p = papel(el);

    if (p === 'linha') {
      // Linha de tabela vira uma linha só: células unidas por " | ". Os interativos de dentro
      // seguem como nós próprios, para poderem ser clicados.
      const cels = [...el.children].map(c => curto(limpar(c.innerText), 80));
      while (cels.length && !cels[0]) cels.shift();
      if (cels.some(c => c)) {
        nos.push({ ref: ref(el), papel: 'linha', texto: cels.join(' | '), prof,
          visivel: !esconde, naTela: !esconde && naTela(el), y: Math.round(el.getBoundingClientRect().top) });
      }
      el.querySelectorAll('a[href],button,input,select,[role=button],[role=link],[role=checkbox]').forEach(i => {
        const pi = papel(i);
        if (pi) emitirNo(i, pi, prof + 1, esconde);
      });
      return;
    }

    if (p) {
      emitirNo(el, p, prof, esconde);
      if (INTERATIVO.has(p) && p !== 'lista') return; // o nome já leva o texto de dentro
    }

    // Parágrafo com link ou negrito no meio: sai inteiro, senão a frase vira pedaços. Os links de
    // dentro seguem como nós próprios, para poderem ser clicados.
    let direto = '';
    for (const c of el.childNodes) if (c.nodeType === 3) direto += c.textContent;
    direto = limpar(direto);
    if (direto && !p && [...el.children].every(c => INLINE.has(c.tagName))) {
      const t = curto(limpar(el.innerText), 500);
      if (/[\p{L}\p{N}]/u.test(t)) {
        nos.push({ ref: '', papel: 'texto', texto: t, prof,
          visivel: !esconde, naTela: !esconde && naTela(el), y: Math.round(el.getBoundingClientRect().top) });
      }
      el.querySelectorAll('a[href],button,input,[role=button],[role=link]').forEach(i => {
        const pi = papel(i);
        if (pi) emitirNo(i, pi, prof + 1, esconde || !visivel(i));
      });
      return;
    }
    if (direto && !p && /[\p{L}\p{N}]/u.test(direto)) {
      nos.push({ ref: '', papel: 'texto', texto: curto(direto, 300), prof,
        visivel: !esconde, naTela: !esconde && naTela(el), y: Math.round(el.getBoundingClientRect().top) });
    }

    for (const c of el.children) andar(c, p ? prof + 1 : prof, esconde);
    if (el.shadowRoot) for (const c of el.shadowRoot.children) andar(c, prof, esconde);
  };

  const emitirNo = (el, p, prof, esconde) => {
    let t = curto(nome(el), 120);
    if (p === 'caixa de texto' || p === 'busca') {
      const v = limpar(el.value);
      if (v) t += ` = "${curto(v, 60)}"`;
    }
    if (p === 'caixa de seleção' || p === 'opção' || p === 'chave') t += el.checked || el.getAttribute('aria-checked') === 'true' ? ' [marcado]' : '';
    if (el.disabled || el.getAttribute('aria-disabled') === 'true') t += ' [desativado]';
    const estrutura = ['tabela', 'formulário', 'navegação', 'principal', 'diálogo', 'título'].includes(p);
    nos.push({ ref: (INTERATIVO.has(p) || estrutura) ? ref(el) : '', papel: p,
      texto: ['tabela', 'formulário', 'navegação', 'principal', 'cabeçalho'].includes(p) ? '' : t, prof,
      visivel: !esconde, naTela: !esconde && naTela(el), y: Math.round(el.getBoundingClientRect().top) });
  };

  document.querySelectorAll('[data-aib-ref]').forEach(e => e.removeAttribute('data-aib-ref'));
  andar(document.body, 0, false);
  return nos;
}
