using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIB.Services.Documentos;

/// <summary>
/// Cria e edita .docx — sempre por um plano que o cartão mostra antes, como a planilha.
/// <para>
/// A edição mexe no XML do próprio arquivo (OpenXml), não o regera: o que a ferramenta não
/// entende (cabeçalho, imagens, comentários, campos) passa intacto. Por isso não há lista do
/// "que se perderia", ao contrário do <see cref="EditorDePlanilha"/>.
/// </para>
/// <para>
/// Toda edição de documento que já existe entra como CONTROLE DE ALTERAÇÕES (revisões do Word),
/// em nome da persona: decisão do usuário (19/10/2026) — o texto só muda quando ele aceita cada
/// revisão no Word, e rejeitar devolve o original. Documento novo (criar) não tem revisões.
/// </para>
/// </summary>
public static class EditorDeDocx
{
    // ─────────────────────────────────────────────────────────────────────────
    // Editar
    // ─────────────────────────────────────────────────────────────────────────

    public static Gravacao Planejar(string caminho, JsonElement ops, string autor = "AIB")
    {
        if (Formatos.De(caminho) != Formato.Docx)
            throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' não é .docx: só .docx se edita (PDF só se lê).");
        if (ops.ValueKind != JsonValueKind.Array || ops.GetArrayLength() == 0)
            throw new ErroDeDocumento("'ops' precisa ser uma lista de operações: replace, insert_after, delete ou table.");

        string? impressao = Gravacao.Impressao(caminho);
        var doc = Abrir(caminho);
        var edicao = new Edicao(doc, string.IsNullOrWhiteSpace(autor) ? "AIB" : autor.Trim());

        // Primeiro resolver TODAS as operações contra a numeração original (a que o 'read'
        // mostrou); só depois aplicar. Aplicando uma a uma, "apague o ¶3 e o ¶5" apagaria o
        // ¶3 e o que ERA o ¶6.
        var passos = new List<(int Ordem, bool Apaga, Action Fazer)>();
        int i = 0;
        foreach (var op in ops.EnumerateArray())
        {
            i++;
            if (op.ValueKind != JsonValueKind.Object)
                throw new ErroDeDocumento($"operação {i}: cada operação é um objeto, como {{\"replace\":\"...\",\"with\":\"...\"}}.");
            passos.Add(edicao.Resolver(op, i));
        }

        // As que apagam vão por último: "apague o ¶3 e insira depois do ¶3" ainda acha o ¶3.
        foreach (var p in passos.Where(p => !p.Apaga)) p.Fazer();
        foreach (var p in passos.Where(p => p.Apaga)) p.Fazer();
        edicao.FecharTrocas();

        var todas = edicao.Mudancas.OrderBy(m => m.Ordem).Select(m => m.Mudanca).ToList();
        int n = todas.Count;
        var lista = todas.Take(PlanoDeGravacao.Visiveis).ToList();
        if (n > lista.Count)
        {
            int resto = n - lista.Count;
            lista.Add(new Mudanca(TipoDeMudanca.Mais, resto == 1 ? "+ 1 alteração" : $"+ {resto} alterações", null, null));
        }

        string resumo = n == 1 ? "1 trecho" : $"{n} trechos";
        if (int.TryParse(doc.ExtendedFilePropertiesPart?.Properties?.Pages?.Text, out int paginas) && paginas > 0)
            resumo += paginas == 1 ? " · 1 página" : $" · {paginas} páginas";

        var plano = new PlanoDeGravacao
        {
            Arquivo = Path.GetFullPath(caminho),
            Criar = false,
            Titulo = n == 1 ? "Sugerir 1 alteração neste documento?" : $"Sugerir {n} alterações neste documento?",
            Consequencia = $"As alterações entram como revisões no Word, em nome de {edicao.Autor}: o texto só muda quando você aceita cada uma lá. O documento não pode estar aberto no Word.",
            Resumo = resumo,
            Mudancas = lista,
            Copia = CopiasDeDocumento.Descricao(caminho),
            Impressao = impressao
        };

        return new GravacaoDeDocx(plano, doc, (n == 1 ? "1 alteração sugerida" : $"{n} alterações sugeridas") + " (controle de alterações)");
    }

    /// <summary>O estado de uma edição: os blocos originais e as mudanças, na ordem das operações.</summary>
    private sealed class Edicao
    {
        private readonly WordprocessingDocument _doc;
        private readonly List<BlocosDoWord.Item> _blocos;
        private readonly Dictionary<OpenXmlElement, BlocosDoWord.Item> _porElemento = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<OpenXmlElement> _novos = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<OpenXmlElement, OpenXmlElement> _ultimoInserido = new(ReferenceEqualityComparer.Instance);

        private readonly HashSet<OpenXmlElement> _apagados = new(ReferenceEqualityComparer.Instance);
        private readonly string _data = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        private int _id;

        public Edicao(WordprocessingDocument doc, string autor)
        {
            _doc = doc;
            Autor = autor;
            _blocos = BlocosDoWord.Ler(doc);
            foreach (var b in _blocos) _porElemento[b.Elemento] = b;

            // w:id das revisões precisa ser único no documento: começa depois do maior que já existe
            // (revisões de outros autores, comentários, marcadores).
            _id = Main.Document!.Descendants()
                .SelectMany(e => e.GetAttributes())
                .Where(a => a.LocalName == "id" && a.NamespaceUri == W)
                .Select(a => int.TryParse(a.Value, out int v) ? v : 0)
                .DefaultIfEmpty(0).Max();
        }

        public string Autor { get; }

        public List<(int Ordem, Mudanca Mudanca)> Mudancas { get; } = new();

        private MainDocumentPart Main => _doc.MainDocumentPart!;
        private Body Body => Main.Document!.Body!;

        public (int, bool, Action) Resolver(JsonElement op, int i)
        {
            if (op.TryGetProperty("replace", out _))
            {
                string antigo = Texto(op, "replace", i);
                string novo = Texto(op, "with", i);
                bool todas = op.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.True;
                if (antigo.Length == 0) throw new ErroDeDocumento($"operação {i}: 'replace' vazio; diga o trecho exato que sai.");
                return (i, false, () => Trocar(i, antigo, novo, todas));
            }

            if (op.TryGetProperty("insert_after", out _))
            {
                var alvo = Bloco(Numero(op, "insert_after", i), i);
                string texto = Texto(op, "text", i);
                string? estilo = op.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                var (id, nome) = EstiloPedido(estilo, i);
                return (i, false, () => Inserir(i, alvo, texto, id, nome));
            }

            if (op.TryGetProperty("delete", out _))
            {
                var alvo = Bloco(Numero(op, "delete", i), i);
                return (i, true, () => Apagar(i, alvo));
            }

            if (op.TryGetProperty("table", out _))
            {
                int k = Numero(op, "table", i), r = Numero(op, "row", i), c = Numero(op, "col", i);
                string valor = Texto(op, "value", i);
                var tabelas = _blocos.Where(b => b.Bloco.Tipo == TipoDeBloco.Tabela).ToList();
                if (k < 1 || k > tabelas.Count)
                    throw new ErroDeDocumento($"operação {i}: o documento tem {Plural(tabelas.Count, "tabela", "tabelas")}; não há a tabela {k}.");
                var tabela = (Table)tabelas[k - 1].Elemento;
                // As linhas que o 'read' mostrou: linha já apagada em revisão não conta.
                var linhas = tabela.Elements<TableRow>()
                    .Where(l => l.GetFirstChild<TableRowProperties>()?.GetFirstChild<Deleted>() == null).ToList();
                if (r < 1 || r > linhas.Count)
                    throw new ErroDeDocumento($"operação {i}: a tabela {k} tem {Plural(linhas.Count, "linha", "linhas")}; não há a linha {r}.");
                var celulas = linhas[r - 1].Elements<TableCell>().ToList();
                if (c < 1 || c > celulas.Count)
                    throw new ErroDeDocumento($"operação {i}: a linha {r} da tabela {k} tem {Plural(celulas.Count, "coluna", "colunas")}; não há a coluna {c}.");
                var celula = celulas[c - 1];
                return (i, false, () => Celula(i, celula, $"Tabela {k}, linha {r}, coluna {c}", valor));
            }

            throw new ErroDeDocumento($"operação {i}: não reconheço a operação; use replace, insert_after, delete ou table.");
        }

        private BlocosDoWord.Item Bloco(int n, int i)
        {
            if (_blocos.Count == 0) throw new ErroDeDocumento($"operação {i}: o documento não tem texto; não há ¶{n}.");
            if (n < 1 || n > _blocos.Count)
                throw new ErroDeDocumento($"operação {i}: não há ¶{n}; o documento vai do ¶1 ao ¶{_blocos.Count}.");
            return _blocos[n - 1];
        }

        // ── replace ──────────────────────────────────────────────────────────

        private void Trocar(int i, string antigo, string novo, bool todas)
        {
            var achados = new List<(Paragraph P, List<int> Pos)>();
            foreach (var p in Body.Descendants<Paragraph>())
            {
                var pos = Ocorrencias(BlocosDoWord.Texto(p), antigo);
                if (pos.Count > 0) achados.Add((p, pos));
            }

            int total = achados.Sum(a => a.Pos.Count);
            if (total == 0)
                throw new ErroDeDocumento($"operação {i}: não achei \"{Curto(antigo, 80)}\" no documento. A comparação é exata "
                    + "(maiúsculas, espaços, pontuação); use action='find' para ver o trecho como está escrito.");
            if (total > 1 && !todas)
            {
                var onde = achados.Select(a => Rotulo(a.P)).Distinct();
                throw new ErroDeDocumento($"operação {i}: \"{Curto(antigo, 80)}\" aparece {total} vezes ({string.Join(", ", onde)}). "
                    + "Ponha mais texto em volta para o trecho ser único, ou use all:true para trocar todas.");
            }

            foreach (var (p, pos) in achados)
            {
                if (!_trocados.Any(t => ReferenceEquals(t.P, p)))
                    _trocados.Add((i, p, BlocosDoWord.Texto(p), Onde(p)));

                // Do fim para o começo: trocar um trecho não muda a posição dos anteriores.
                for (int j = pos.Count - 1; j >= 0; j--) Substituir(p, pos[j], antigo.Length, novo);
            }
        }

        /// <summary>Parágrafos trocados: a primeira operação que os tocou, o texto original e onde.</summary>
        private readonly List<(int Ordem, Paragraph P, string Antes, string Onde)> _trocados = new();

        /// <summary>
        /// Uma linha por parágrafo trocado, do texto ORIGINAL ao final. Visto no ensaio de
        /// 19/10/2026: duas trocas no mesmo parágrafo ("80" → "95", "novembro" → "dezembro")
        /// davam duas linhas, e a segunda já mostrava o "95" no "antes".
        /// </summary>
        public void FecharTrocas()
        {
            foreach (var (ordem, p, antes, onde) in _trocados)
            {
                string depois = BlocosDoWord.Texto(p);
                if (depois == antes) continue;

                int ini = 0;
                while (ini < antes.Length && ini < depois.Length && antes[ini] == depois[ini]) ini++;
                int fimA = antes.Length, fimD = depois.Length;
                while (fimA > ini && fimD > ini && antes[fimA - 1] == depois[fimD - 1]) { fimA--; fimD--; }

                Mudancas.Add((ordem, new Mudanca(TipoDeMudanca.Valor, onde, Janela(antes, ini, fimA), Janela(depois, ini, fimD))));
            }
        }

        /// <summary>O trecho que mudou, com até 60 caracteres de cada lado.</summary>
        private static string Janela(string texto, int ini, int fim)
        {
            int a = Math.Max(0, ini - 60), b = Math.Min(texto.Length, fim + 60);
            return (a > 0 ? "…" : "") + texto[a..b] + (b < texto.Length ? "…" : "");
        }

        /// <summary>
        /// Marca [x, x+n) do texto do parágrafo como apagado (w:del) e põe o texto novo logo depois
        /// como inserido (w:ins), com a formatação do run onde o trecho começa. Atravessa runs: cada
        /// run tocado se parte em antes / dentro / depois, e só o "dentro" vai para o w:del. Trocar
        /// "Prazo: 30 dias" com "30" em negrito num run à parte sugere o "45" em negrito também.
        /// </summary>
        private void Substituir(Paragraph p, int x, int n, string novo)
        {
            int fim = x + n;
            var pedacos = BlocosDoWord.Pedacos(p);
            var mapa = pedacos.ToDictionary(q => q.Elemento, q => q, (IEqualityComparer<OpenXmlElement>)ReferenceEqualityComparer.Instance);
            var runs = pedacos.Where(q => q.Fim > x && q.Inicio < fim)
                              .Select(q => q.Elemento.Parent).OfType<Run>().Distinct().ToList();
            var formato = runs.FirstOrDefault()?.RunProperties?.CloneNode(true) as RunProperties;
            OpenXmlElement? ultimo = null;

            foreach (var r in runs)
            {
                var antes = new List<OpenXmlElement>();
                var dentro = new List<OpenXmlElement>();
                var depois = new List<OpenXmlElement>();
                int pos = r.ChildElements.Select(c => mapa.TryGetValue(c, out var q) ? q.Inicio : -1).First(v => v >= 0);

                foreach (var c in r.ChildElements)
                {
                    if (c is RunProperties) continue;
                    if (!mapa.TryGetValue(c, out var q))
                    {
                        // Sem texto (desenho, campo): fica do lado do trecho em que está.
                        bool meio = pos > x && pos < fim;
                        (pos <= x ? antes : pos >= fim ? depois : dentro).Add(Apagado(c.CloneNode(true), meio));
                        continue;
                    }

                    pos = q.Fim;
                    if (q.Fim <= x) antes.Add(c.CloneNode(true));
                    else if (q.Inicio >= fim) depois.Add(c.CloneNode(true));
                    else if (c is Text)
                    {
                        int a = Math.Max(x, q.Inicio) - q.Inicio, b = Math.Min(fim, q.Fim) - q.Inicio;
                        if (a > 0) antes.Add(new Text(q.Texto[..a]) { Space = SpaceProcessingModeValues.Preserve });
                        dentro.Add(new DeletedText(q.Texto[a..b]) { Space = SpaceProcessingModeValues.Preserve });
                        if (b < q.Texto.Length) depois.Add(new Text(q.Texto[b..]) { Space = SpaceProcessingModeValues.Preserve });
                    }
                    else dentro.Add(c.CloneNode(true)); // tab ou quebra inteiro dentro do trecho
                }

                if (antes.Count > 0) r.InsertBeforeSelf(Parte(r, antes));
                if (dentro.Count > 0) ultimo = r.InsertBeforeSelf(Rev(new DeletedRun(Parte(r, dentro))));
                if (depois.Count > 0) r.InsertBeforeSelf(Parte(r, depois));
                r.Remove();
            }

            if (novo.Length == 0 || ultimo == null) return;

            // Dentro do w:ins de outro autor não cabe um w:ins nosso: o dele se parte em dois no
            // ponto da troca (o resto ganha w:id novo, mesmo autor e data), e o nosso entra no meio.
            // Pôr o nosso depois do dele inteiro dava "Valor:  reais250" em vez de "Valor: 250 reais".
            while (ultimo.Parent is InsertedRun fora)
            {
                var resto = ultimo.ElementsAfter().ToList();
                if (resto.Count > 0)
                {
                    var segunda = (InsertedRun)fora.CloneNode(false);
                    segunda.SetAttribute(new OpenXmlAttribute("w", "id", W, (++_id).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    foreach (var c in resto)
                    {
                        c.Remove();
                        segunda.Append(c);
                    }
                    fora.InsertAfterSelf(segunda);
                }
                ultimo = fora;
            }
            ultimo.InsertAfterSelf(Rev(new InsertedRun(Casa.Run(novo, formato))));
        }

        /// <summary>Um run com a formatação (e os atributos) de <paramref name="modelo"/> e estes filhos.</summary>
        private static Run Parte(Run modelo, List<OpenXmlElement> filhos)
        {
            var r = (Run)modelo.CloneNode(false);
            if (modelo.RunProperties != null) r.Append(modelo.RunProperties.CloneNode(true));
            r.Append(filhos);
            return r;
        }

        /// <summary>Código de campo dentro de w:del precisa ser w:delInstrText, ou o Word acusa arquivo corrompido.</summary>
        private static OpenXmlElement Apagado(OpenXmlElement c, bool apagar) =>
            apagar && c is FieldCode fc ? new DeletedFieldCode(fc.Text) { Space = SpaceProcessingModeValues.Preserve } : c;

        /// <summary>Põe todos os runs do parágrafo (os que ainda estão de pé) dentro de w:del.</summary>
        private void ApagarRuns(Paragraph p)
        {
            foreach (var r in p.Descendants<Run>().Where(r => DePe(r, p)).ToList())
            {
                if (r.ChildElements.All(c => c is RunProperties)) continue;
                foreach (var t in r.Elements<Text>().ToList())
                {
                    t.InsertBeforeSelf(new DeletedText(t.Text) { Space = SpaceProcessingModeValues.Preserve });
                    t.Remove();
                }
                foreach (var f in r.Elements<FieldCode>().ToList())
                {
                    f.InsertBeforeSelf(Apagado(f.CloneNode(true), true));
                    f.Remove();
                }
                var d = r.InsertBeforeSelf(Rev(new DeletedRun()));
                r.Remove();
                d.Append(r);
            }
        }

        /// <summary>Run do próprio parágrafo (não de caixa de texto aninhada) e fora de revisão apagada.</summary>
        private static bool DePe(Run r, Paragraph p)
        {
            for (var a = r.Parent; a != null && a != p; a = a.Parent)
                if (a is DeletedRun or MoveFromRun or Paragraph) return false;
            return true;
        }

        /// <summary>Marca de parágrafo (¶) inserida ou apagada: sem ela, aceitar não junta nem separa os parágrafos.</summary>
        private static void Marcar(Paragraph p, OpenXmlElement marca)
        {
            var pPr = p.ParagraphProperties ??= new ParagraphProperties();
            var rPr = pPr.ParagraphMarkRunProperties ??= new ParagraphMarkRunProperties();

            // No XML, w:ins vem antes de w:del, e os dois antes da formatação.
            if (marca is Deleted && rPr.GetFirstChild<Inserted>() is { } ins) ins.InsertAfterSelf(marca);
            else rPr.PrependChild(marca);
        }

        /// <summary>Autor, data e um w:id ainda livre. A data só vai ao XML, não ao plano: o plano é refeito e comparado.</summary>
        private T Rev<T>(T el) where T : OpenXmlElement
        {
            el.SetAttribute(new OpenXmlAttribute("w", "id", W, (++_id).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            el.SetAttribute(new OpenXmlAttribute("w", "author", W, Autor));
            el.SetAttribute(new OpenXmlAttribute("w", "date", W, _data));
            return el;
        }

        private static List<int> Ocorrencias(string texto, string alvo)
        {
            var pos = new List<int>();
            for (int x = texto.IndexOf(alvo, StringComparison.Ordinal); x >= 0;
                 x = texto.IndexOf(alvo, x + alvo.Length, StringComparison.Ordinal))
                pos.Add(x);
            return pos;
        }

        // ── insert_after ─────────────────────────────────────────────────────

        private (string? Id, string Nome) EstiloPedido(string? pedido, int i)
        {
            switch (pedido?.Trim().ToLowerInvariant())
            {
                case null or "" or "normal": return (null, "Normal");
                case "heading1" or "heading2" or "heading3":
                    int n = pedido.Trim()[^1] - '0';
                    return (Casa.Titulo(Main, n), $"Título {n}");
                case "list": return (Casa.Lista(Main), "Lista");
                default:
                    throw new ErroDeDocumento($"operação {i}: style \"{pedido}\" não existe; use Heading1, Heading2, Heading3, List ou Normal.");
            }
        }

        private void Inserir(int i, BlocosDoWord.Item alvo, string texto, string? estilo, string nome)
        {
            var depois = _ultimoInserido.TryGetValue(alvo.Elemento, out var u) ? u : alvo.Elemento;
            foreach (var linha in Linhas(texto))
            {
                var p = Casa.Paragrafo(linha, estilo);
                foreach (var r in p.Elements<Run>().ToList())
                {
                    var ins = r.InsertBeforeSelf(Rev(new InsertedRun()));
                    r.Remove();
                    ins.Append(r);
                }
                Marcar(p, Rev(new Inserted()));
                depois.InsertAfterSelf(p);
                _novos.Add(p);
                depois = p;
            }
            _ultimoInserido[alvo.Elemento] = depois;

            string mostra = string.Join(" / ", Linhas(texto));
            if (estilo != null) mostra = $"[{nome}] {mostra}";
            Mudancas.Add((i, new Mudanca(TipoDeMudanca.Entra, $"depois do ¶{alvo.Bloco.Numero}", null, mostra)));
        }

        // ── delete ───────────────────────────────────────────────────────────

        /// <summary>
        /// O bloco fica no arquivo, marcado como apagado: o texto em w:del e a marca de parágrafo
        /// apagada (na tabela, cada linha apagada). Aceitar no Word o remove; rejeitar o devolve.
        /// </summary>
        private void Apagar(int i, BlocosDoWord.Item alvo)
        {
            if (!_apagados.Add(alvo.Elemento))
                throw new ErroDeDocumento($"operação {i}: o ¶{alvo.Bloco.Numero} já foi apagado por outra operação.");

            if (alvo.Elemento is Table t)
            {
                foreach (var linha in t.Elements<TableRow>())
                {
                    var trPr = linha.GetFirstChild<TableRowProperties>();
                    if (trPr == null)
                    {
                        trPr = new TableRowProperties();
                        if (linha.GetFirstChild<TablePropertyExceptions>() is { } ex) ex.InsertAfterSelf(trPr);
                        else linha.PrependChild(trPr);
                    }
                    if (trPr.GetFirstChild<Deleted>() != null) continue;

                    // Em trPr, as marcas vêm depois das propriedades e antes de trPrChange.
                    var del = Rev(new Deleted());
                    if (trPr.GetFirstChild<TableRowPropertiesChange>() is { } mudou) mudou.InsertBeforeSelf(del);
                    else trPr.Append(del);
                    foreach (var p in linha.Descendants<Paragraph>().ToList()) ApagarRuns(p);
                }
            }
            else
            {
                var p = (Paragraph)alvo.Elemento;
                ApagarRuns(p);
                Marcar(p, Rev(new Deleted()));
            }

            var b = alvo.Bloco;
            string antes = b.Tipo == TipoDeBloco.Tabela && b.Tabela != null
                ? $"[Tabela {b.Tabela.Count}×{b.Tabela.Max(l => (int?)l.Count) ?? 0}] {Curto(string.Join(" | ", b.Tabela.FirstOrDefault() ?? Array.Empty<string>()), 120)}"
                : Curto(b.Texto, 300);
            Mudancas.Add((i, new Mudanca(TipoDeMudanca.Sai, $"¶{b.Numero}", antes, null)));
        }

        // ── table ────────────────────────────────────────────────────────────

        /// <summary>
        /// O texto antigo da célula em w:del, o novo em w:ins no primeiro parágrafo, com a
        /// formatação do primeiro run. Os parágrafos de cima perdem a marca: aceitar deixa um só.
        /// </summary>
        private void Celula(int i, TableCell celula, string onde, string valor)
        {
            string antes = BlocosDoWord.TextoDaCelula(celula);
            var rPr = celula.Descendants<Run>().FirstOrDefault(r => !r.Ancestors<DeletedRun>().Any())
                            ?.RunProperties?.CloneNode(true) as RunProperties;
            var ps = celula.Elements<Paragraph>().ToList();
            if (ps.Count == 0) ps.Add(celula.AppendChild(new Paragraph()));

            foreach (var p in ps) ApagarRuns(p);
            for (int j = 0; j < ps.Count - 1; j++)
                if (ps[j].ParagraphProperties?.ParagraphMarkRunProperties?.GetFirstChild<Deleted>() == null)
                    Marcar(ps[j], Rev(new Deleted()));
            if (valor.Length > 0) ps[0].Append(Rev(new InsertedRun(Casa.Run(valor, rPr))));

            Mudancas.Add((i, new Mudanca(TipoDeMudanca.Valor, onde, antes.Length == 0 ? null : antes, valor.Length == 0 ? null : valor)));
        }

        // ── onde ─────────────────────────────────────────────────────────────

        /// <summary>O bloco (original) que contém o parágrafo, ou null se o parágrafo é novo/sem número.</summary>
        private BlocosDoWord.Item? Dono(OpenXmlElement p)
        {
            for (OpenXmlElement? a = p; a != null; a = a.Parent)
                if (_porElemento.TryGetValue(a, out var b)) return b;
            return null;
        }

        private string Rotulo(Paragraph p) =>
            Dono(p) is { } b ? $"¶{b.Bloco.Numero}" : _novos.Contains(p) ? "parágrafo novo" : "parágrafo sem número";

        private string Onde(Paragraph p)
        {
            if (Dono(p) is not { } b) return Rotulo(p);

            if (b.Elemento is Table t)
            {
                var celula = p.Ancestors<TableCell>().FirstOrDefault(c => c.Parent?.Parent == t);
                if (celula?.Parent is TableRow linha)
                {
                    int r = t.Elements<TableRow>().TakeWhile(x => x != linha).Count() + 1;
                    int c = linha.Elements<TableCell>().TakeWhile(x => x != celula).Count() + 1;
                    return $"Tabela ¶{b.Bloco.Numero}, linha {r}, coluna {c}";
                }
                return $"Tabela ¶{b.Bloco.Numero}";
            }

            return b.Bloco.Secao != null ? $"Seção {Curto(b.Bloco.Secao, 40)} · ¶{b.Bloco.Numero}" : $"¶{b.Bloco.Numero}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Criar
    // ─────────────────────────────────────────────────────────────────────────

    public static Gravacao PlanejarCriacao(string caminho, JsonElement conteudo)
    {
        if (Formatos.PelaExtensao(caminho) != Formato.Docx)
            throw new ErroDeDocumento("documento novo precisa terminar em .docx.");
        if (conteudo.ValueKind != JsonValueKind.Object || !conteudo.TryGetProperty("blocks", out var blocos)
            || blocos.ValueKind != JsonValueKind.Array || blocos.GetArrayLength() == 0)
            throw new ErroDeDocumento("'content' de documento precisa de 'blocks': [{style, text} | {list:[...]} | {table:[[...]]}].");

        bool existe = File.Exists(caminho);
        var mem = new MemoryStream();
        var doc = WordprocessingDocument.Create(mem, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;

        string?[] titulos = { null, Casa.Titulo(main, 1), Casa.Titulo(main, 2), Casa.Titulo(main, 3) };
        string lista = Casa.Lista(main);

        var previa = new List<string>();
        int i = 0, tabelas = 0;
        foreach (var b in blocos.EnumerateArray())
        {
            i++;
            if (b.ValueKind != JsonValueKind.Object)
                throw new ErroDeDocumento($"bloco {i}: use {{style, text}}, {{list:[...]}} ou {{table:[[...]]}}.");

            if (b.TryGetProperty("table", out var t))
            {
                var linhas = LinhasDaTabela(t, i);
                body.Append(Casa.Tabela(linhas));
                tabelas++;
                previa.Add($"[Tabela {linhas.Count}×{linhas.Max(l => l.Count)}] {Curto(string.Join(" | ", linhas[0]), 60)}");
            }
            else if (b.TryGetProperty("list", out var l))
            {
                if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() == 0)
                    throw new ErroDeDocumento($"bloco {i}: 'list' precisa ser uma lista de textos.");
                foreach (var item in l.EnumerateArray())
                {
                    string s = Valor(item);
                    body.Append(Casa.Paragrafo(s, lista));
                    previa.Add("• " + Curto(s, 80));
                }
            }
            else if (b.TryGetProperty("text", out var tx))
            {
                string s = Valor(tx);
                string? estilo = b.TryGetProperty("style", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()?.Trim() : null;
                int nivel = estilo?.ToLowerInvariant() switch
                {
                    null or "" or "normal" => 0,
                    "heading1" => 1,
                    "heading2" => 2,
                    "heading3" => 3,
                    _ => throw new ErroDeDocumento($"bloco {i}: style \"{estilo}\" não existe; use Heading1, Heading2, Heading3 ou Normal.")
                };
                foreach (var linha in Linhas(s)) body.Append(Casa.Paragrafo(linha, titulos[nivel]));
                previa.Add((nivel > 0 ? $"[Título {nivel}] " : "") + Curto(Apresentar(s), 80));
            }
            else
                throw new ErroDeDocumento($"bloco {i}: use {{style, text}}, {{list:[...]}} ou {{table:[[...]]}}.");
        }

        // O Word não deixa o documento terminar numa tabela: sem um parágrafo depois, ele acrescenta um ao abrir.
        if (body.LastChild is Table) body.Append(new Paragraph());
        // A4 com as margens do Word em português (3 cm dos lados, 2,5 cm em cima e embaixo).
        body.Append(new SectionProperties(
            new PageSize { Width = 11906U, Height = 16838U },
            new PageMargin { Top = 1417, Right = 1701U, Bottom = 1417, Left = 1701U, Header = 708U, Footer = 708U, Gutter = 0U }));

        var plano = new PlanoDeGravacao
        {
            Arquivo = Path.GetFullPath(caminho),
            Criar = !existe,
            Titulo = existe ? "Substituir este documento inteiro?" : "Criar este documento?",
            Consequencia = existe
                ? "O arquivo existe: TODO o conteúdo atual é trocado por este."
                : "Arquivo novo; nada que já existe é tocado.",
            Resumo = $"{Plural(i, "bloco", "blocos")} · {Plural(tabelas, "tabela", "tabelas")}",
            Previa = new PreviaDeArquivo(Array.Empty<string>(),
                                         previa.Take(6).Select(s => (IReadOnlyList<string>)new[] { s }).ToList(),
                                         Math.Max(0, previa.Count - 6)),
            Copia = existe ? CopiasDeDocumento.Descricao(caminho) : null,
            Impressao = existe ? Gravacao.Impressao(caminho) : null
        };

        return new GravacaoDeDocx(plano, doc, existe ? "documento substituído" : "documento criado");
    }

    private static List<List<string>> LinhasDaTabela(JsonElement t, int i)
    {
        if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() == 0)
            throw new ErroDeDocumento($"bloco {i}: 'table' precisa ser uma lista de linhas, cada uma uma lista de células.");
        var linhas = new List<List<string>>();
        foreach (var l in t.EnumerateArray())
        {
            if (l.ValueKind != JsonValueKind.Array)
                throw new ErroDeDocumento($"bloco {i}: cada linha da tabela é uma lista de células, como [\"a\",\"b\"].");
            linhas.Add(l.EnumerateArray().Select(Valor).ToList());
        }
        if (linhas.All(l => l.Count == 0)) throw new ErroDeDocumento($"bloco {i}: a tabela não tem células.");
        return linhas;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Estilos, parágrafos e tabelas novos
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Os estilos que a ferramenta usa. Procura pelo NOME o que o documento já tem (o "Título 1"
    /// do usuário, com a fonte e a cor dele) e só cria o que falta. Tudo determinístico: o plano
    /// é refeito na execução e precisa sair igual.
    /// </summary>
    private static class Casa
    {
        public static string Titulo(MainDocumentPart main, int n)
        {
            var estilos = Estilos(main);
            if (PorNome(estilos, $"heading {n}") is { } id) return id;

            string normal = Normal(main);
            string novo = Livre(estilos, $"Heading{n}");
            estilos.Append(new Style(
                new StyleName { Val = $"heading {n}" },
                new BasedOn { Val = normal },
                new NextParagraphStyle { Val = normal },
                new PrimaryStyle(),
                new StyleParagraphProperties(
                    new KeepNext(),
                    new SpacingBetweenLines { Before = "240", After = "120" },
                    new OutlineLevel { Val = n - 1 }),
                new StyleRunProperties(
                    new Bold(),
                    new FontSize { Val = (n switch { 1 => 32, 2 => 28, _ => 24 }).ToString() }))
            { Type = StyleValues.Paragraph, StyleId = novo });
            return novo;
        }

        /// <summary>
        /// "List Bullet" com marcador de verdade (numeração do Word), não "• " no texto: assim o
        /// leitor o vê como item, e o usuário edita a lista no Word como qualquer lista.
        /// </summary>
        public static string Lista(MainDocumentPart main)
        {
            var estilos = Estilos(main);
            if (PorNome(estilos, "List Bullet") is { } id) return id;

            string normal = Normal(main);
            int num = Marcadores(main);
            string novo = Livre(estilos, "ListBullet");
            estilos.Append(new Style(
                new StyleName { Val = "List Bullet" },
                new BasedOn { Val = normal },
                new PrimaryStyle(),
                new StyleParagraphProperties(
                    new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = num }),
                    new Indentation { Left = "720", Hanging = "360" }))
            { Type = StyleValues.Paragraph, StyleId = novo });
            return novo;
        }

        private static int Marcadores(MainDocumentPart main)
        {
            var parte = main.NumberingDefinitionsPart ?? main.AddNewPart<NumberingDefinitionsPart>();
            var numeracao = parte.Numbering ??= new Numbering();

            int abs = numeracao.Elements<AbstractNum>().Select(a => a.AbstractNumberId?.Value ?? 0).DefaultIfEmpty(-1).Max() + 1;
            int num = numeracao.Elements<NumberingInstance>().Select(n => n.NumberID?.Value ?? 0).DefaultIfEmpty(0).Max() + 1;

            var a = new AbstractNum(
                new MultiLevelType { Val = MultiLevelValues.SingleLevel },
                new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = NumberFormatValues.Bullet },
                    new LevelText { Val = "•" },
                    new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(new Indentation { Left = "720", Hanging = "360" }))
                { LevelIndex = 0 })
            { AbstractNumberId = abs };
            var instancia = new NumberingInstance(new AbstractNumId { Val = abs }) { NumberID = num };

            // A ordem do XML importa: todos os abstractNum antes de todos os num.
            if (numeracao.Elements<AbstractNum>().LastOrDefault() is { } ultimoAbs) ultimoAbs.InsertAfterSelf(a);
            else if (numeracao.Elements<NumberingInstance>().FirstOrDefault() is { } primeiroNum) primeiroNum.InsertBeforeSelf(a);
            else numeracao.Append(a);

            if (numeracao.Elements<NumberingInstance>().LastOrDefault() is { } ultimoNum) ultimoNum.InsertAfterSelf(instancia);
            else a.InsertAfterSelf(instancia);

            return num;
        }

        private static string Normal(MainDocumentPart main)
        {
            var estilos = Estilos(main);
            var achado = estilos.Elements<Style>().FirstOrDefault(s => s.Type?.Value == StyleValues.Paragraph && s.Default?.Value == true)
                         ?? estilos.Elements<Style>().FirstOrDefault(s => string.Equals(s.StyleName?.Val?.Value, "Normal", StringComparison.OrdinalIgnoreCase));
            if (achado?.StyleId?.Value is { } id) return id;

            string novo = Livre(estilos, "Normal");
            estilos.Append(new Style(new StyleName { Val = "Normal" }, new PrimaryStyle())
                { Type = StyleValues.Paragraph, StyleId = novo, Default = true });
            return novo;
        }

        private static Styles Estilos(MainDocumentPart main)
        {
            var parte = main.StyleDefinitionsPart ?? main.AddNewPart<StyleDefinitionsPart>();
            return parte.Styles ??= new Styles();
        }

        private static string? PorNome(Styles estilos, string nome) =>
            estilos.Elements<Style>()
                   .FirstOrDefault(s => s.Type?.Value == StyleValues.Paragraph
                                        && string.Equals(s.StyleName?.Val?.Value, nome, StringComparison.OrdinalIgnoreCase))
                   ?.StyleId?.Value;

        private static string Livre(Styles estilos, string id)
        {
            var usados = estilos.Elements<Style>().Select(s => s.StyleId?.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!usados.Contains(id)) return id;
            for (int n = 2; ; n++)
                if (!usados.Contains($"{id}AIB{n}")) return $"{id}AIB{n}";
        }

        public static Paragraph Paragrafo(string texto, string? estilo)
        {
            var p = new Paragraph();
            if (estilo != null) p.Append(new ParagraphProperties(new ParagraphStyleId { Val = estilo }));
            if (texto.Length > 0) p.Append(Run(texto, null));
            return p;
        }

        /// <summary>Um run com o texto; '\n' vira quebra de linha dentro do mesmo parágrafo.</summary>
        public static Run Run(string texto, RunProperties? rPr)
        {
            var r = new Run();
            if (rPr != null) r.Append(rPr);
            var linhas = Limpo(texto).Replace("\r\n", "\n").Split('\n');
            for (int j = 0; j < linhas.Length; j++)
            {
                if (j > 0) r.Append(new Break());
                if (linhas[j].Length > 0) r.Append(new Text(linhas[j]) { Space = SpaceProcessingModeValues.Preserve });
            }
            return r;
        }

        public static Table Tabela(List<List<string>> linhas)
        {
            int colunas = Math.Max(1, linhas.Max(l => l.Count));
            OpenXmlElement borda(OpenXmlElement b)
            {
                var e = (BorderType)b;
                e.Val = BorderValues.Single;
                e.Size = 4U;
                e.Color = "auto";
                return e;
            }

            var t = new Table(
                new TableProperties(
                    new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                    new TableBorders(
                        borda(new TopBorder()), borda(new LeftBorder()), borda(new BottomBorder()),
                        borda(new RightBorder()), borda(new InsideHorizontalBorder()), borda(new InsideVerticalBorder()))),
                new TableGrid(Enumerable.Range(0, colunas).Select(_ => new GridColumn { Width = (9000 / colunas).ToString() })));

            for (int r = 0; r < linhas.Count; r++)
            {
                var linha = new TableRow();
                for (int c = 0; c < colunas; c++)
                {
                    string s = c < linhas[r].Count ? linhas[r][c] : "";
                    var p = new Paragraph();
                    if (s.Length > 0) p.Append(Run(s, r == 0 ? new RunProperties(new Bold()) : null));
                    linha.Append(new TableCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Auto }), p));
                }
                t.Append(linha);
            }
            return t;
        }

        /// <summary>Caracteres de controle não cabem em XML: o Word recusaria abrir o arquivo.</summary>
        private static string Limpo(string s) =>
            s.Any(ch => ch < ' ' && ch != '\t' && ch != '\n' && ch != '\r')
                ? new string(s.Where(ch => ch >= ' ' || ch == '\t' || ch == '\n' || ch == '\r').ToArray())
                : s;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Abrir e gravar
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Uma cópia na memória: planejar não toca o arquivo, e o Word aberto não impede ler.</summary>
    private static WordprocessingDocument Abrir(string caminho)
    {
        var mem = new MemoryStream();
        using (var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            fs.CopyTo(mem);
        mem.Position = 0;

        try
        {
            return WordprocessingDocument.Open(mem, true);
        }
        catch (Exception e) when (e is OpenXmlPackageException or InvalidDataException or System.Xml.XmlException)
        {
            throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' não é um .docx válido.");
        }
    }

    private sealed class GravacaoDeDocx : Gravacao
    {
        private readonly WordprocessingDocument _doc;

        public GravacaoDeDocx(PlanoDeGravacao plano, WordprocessingDocument doc, string resultado) : base(plano)
        {
            _doc = doc;
            Resultado = resultado;
        }

        public override string Resultado { get; }

        protected override void GravarEm(string caminho)
        {
            using (_doc.Clone(caminho)) { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Miúdos
    // ─────────────────────────────────────────────────────────────────────────

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string Texto(JsonElement op, string campo, int i)
    {
        if (!op.TryGetProperty(campo, out var v) || v.ValueKind == JsonValueKind.Null)
            throw new ErroDeDocumento($"operação {i}: falta '{campo}'.");
        return Valor(v);
    }

    private static string Valor(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => v.GetRawText()
    };

    /// <summary>Número de bloco, tabela, linha ou coluna. Aceita "¶12" e "12", que o modelo às vezes manda.</summary>
    private static int Numero(JsonElement op, string campo, int i)
    {
        if (!op.TryGetProperty(campo, out var v)) throw new ErroDeDocumento($"operação {i}: falta '{campo}'.");
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString()?.Trim().TrimStart('¶'), out n)) return n;
        throw new ErroDeDocumento($"operação {i}: '{campo}' precisa ser um número inteiro.");
    }

    private static IEnumerable<string> Linhas(string texto) => texto.Replace("\r\n", "\n").Split('\n');

    private static string Apresentar(string s) => s.Replace("\r\n", " / ").Replace("\n", " / ");

    private static string Curto(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Plural(int n, string um, string varios) => n == 1 ? $"1 {um}" : $"{n} {varios}";
}
