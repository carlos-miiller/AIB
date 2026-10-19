using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace AIB.Services.Documentos;

/// <summary>
/// Cria e edita planilhas (.xlsx, .xlsm, .csv) — sempre por um plano que o cartão mostra antes.
/// <para>
/// O .csv passa pela mesma máquina: vira uma pasta de trabalho na memória, recebe as operações
/// e volta a ser texto com o separador e a codificação que tinha.
/// </para>
/// </summary>
public static class EditorDePlanilha
{
    /// <summary>
    /// Acima disto a edição é PESADA: o efeito nas fórmulas continua sendo calculado, mas o
    /// cartão avisa em destaque que preparar e gravar vão demorar (decisão do usuário,
    /// 19/10/2026: calcular sempre, e deixar claro quando é lento).
    /// </summary>
    public const int FormulasDeEdicaoPesada = 20000;

    /// <summary>Células editadas de uma vez a partir das quais a edição também é pesada.</summary>
    public const int CelulasDeEdicaoPesada = 10_000;

    /// <summary>Células que uma edição grava, no máximo.</summary>
    public const int TetoDeCelulas = 100_000;

    // ─────────────────────────────────────────────────────────────────────────
    // Editar
    // ─────────────────────────────────────────────────────────────────────────

    public static Gravacao Planejar(string caminho, JsonElement ops, string? abaPadrao)
    {
        var formato = Formatos.De(caminho);
        RecusarOQueSePerderia(caminho, formato);

        string? impressao = Gravacao.Impressao(caminho);
        var (wb, csv) = Abrir(caminho, formato);

        var antes = formato == Formato.Csv ? new Dictionary<string, string>() : ValoresDasFormulas(wb);
        var refsAntes = FormulasComRef(wb);

        var mudancas = new List<(string Aba, int Linha, int Coluna, string? Antes)>();
        var entram = new List<Mudanca>();
        var saem = new List<Mudanca>();
        var outras = new List<Mudanca>();
        var grupos = new List<Grupo>();
        bool estrutural = false;
        int celulas = 0;

        int i = 0;
        foreach (var op in ops.EnumerateArray())
        {
            i++;
            if (op.ValueKind != JsonValueKind.Object) throw new ErroDeDocumento($"a operação {i} não é um objeto.");

            var ws = Aba(wb, Texto(op, "sheet") ?? abaPadrao, i);

            if (Texto(op, "cell") is { } celula)
            {
                var c = Celula(ws, celula, i);
                mudancas.Add((ws.Name, c.Address.RowNumber, c.Address.ColumnNumber, Mostrar(c)));
                Escrever(c, Valor(op, i));
                celulas++;
            }
            else if (Texto(op, "range") is { } faixa)
            {
                var r = Faixa(ws, faixa, i);
                var valor = Valor(op, i);
                var g = new Grupo(ws.Name, r.FirstColumn().ColumnNumber(), r.LastColumn().ColumnNumber(),
                                  r.FirstRow().RowNumber(), r.LastRow().RowNumber(), valor is string s && s.StartsWith('='));
                foreach (var c in r.Cells(false))
                {
                    if (++celulas > TetoDeCelulas) throw new ErroDeDocumento($"a edição passa de {TetoDeCelulas} células.");
                    g.Celulas.Add((c.Address.RowNumber, c.Address.ColumnNumber, Mostrar(c)));
                    Escrever(c, valor is string t ? t.Replace("{row}", c.Address.RowNumber.ToString(CultureInfo.InvariantCulture)) : valor);
                }
                grupos.Add(g);
            }
            else if (op.TryGetProperty("insert_rows", out var ins))
            {
                if (!ins.TryGetInt32(out int antesDe) || antesDe < 1) throw new ErroDeDocumento($"operação {i}: 'insert_rows' é o número da linha (antes da qual inserir).");
                var linhas = Linhas(op, "rows", i);
                if (linhas.Count == 0) throw new ErroDeDocumento($"operação {i}: 'insert_rows' precisa de 'rows' com as linhas novas.");

                ws.Row(antesDe).InsertRowsAbove(linhas.Count);
                for (int k = 0; k < linhas.Count; k++)
                {
                    for (int col = 0; col < linhas[k].Count; col++)
                        Escrever(ws.Cell(antesDe + k, col + 1), linhas[k][col]);
                    entram.Add(new Mudanca(TipoDeMudanca.Entra, (antesDe + k).ToString(CultureInfo.InvariantCulture), null,
                        string.Join(" | ", linhas[k].Select(v => v?.ToString() ?? ""))));
                }
                estrutural = true;
            }
            else if (op.TryGetProperty("delete_rows", out _))
            {
                var (de, ate) = LinhasDe(op, i);
                for (int r = de; r <= ate; r++)
                {
                    var conteudo = ws.Row(r).CellsUsed().Select(Formatado).Where(t => t.Length > 0);
                    string texto = string.Join(" | ", conteudo);
                    saem.Add(new Mudanca(TipoDeMudanca.Sai, r.ToString(CultureInfo.InvariantCulture), texto.Length == 0 ? "(linha em branco)" : texto, null));
                }
                AnularReferencias(wb, ws, de, ate);
                ws.Rows(de, ate).Delete();
                estrutural = true;
            }
            else if (Texto(op, "add_sheet") is { } nova)
            {
                if (wb.Worksheets.Contains(nova)) throw new ErroDeDocumento($"operação {i}: já existe a aba \"{nova}\".");
                wb.AddWorksheet(nova);
                outras.Add(new Mudanca(TipoDeMudanca.Valor, "aba nova", null, nova));
            }
            else if (Texto(op, "rename_sheet") is { } velho)
            {
                var alvo = Aba(wb, velho, i);
                string novo = Texto(op, "to") ?? throw new ErroDeDocumento($"operação {i}: 'rename_sheet' precisa de 'to'.");
                outras.Add(new Mudanca(TipoDeMudanca.Valor, "nome da aba", alvo.Name, novo));
                alvo.Name = novo;
            }
            else
            {
                throw new ErroDeDocumento($"operação {i} desconhecida. Use cell, range, insert_rows, delete_rows, add_sheet ou rename_sheet.");
            }
        }

        if (formato != Formato.Csv) Recalcular(wb);

        // ── o plano ──
        var lista = new List<Mudanca>();
        var contagens = new List<string>();
        int total = mudancas.Count + grupos.Sum(g => g.Celulas.Count);
        bool resumir = total + entram.Count + saem.Count + outras.Count > PlanoDeGravacao.Visiveis;

        foreach (var (aba, l, c, ant) in mudancas.Take(PlanoDeGravacao.Visiveis))
        {
            var cel = wb.Worksheet(aba).Cell(l, c);
            lista.Add(new Mudanca(TipoDeMudanca.Valor, Endereco(wb, aba, l, c), ant, Mostrar(cel), Erro(cel)));
        }
        if (mudancas.Count > PlanoDeGravacao.Visiveis)
            lista.Add(new Mudanca(TipoDeMudanca.Mais, $"+ {mudancas.Count - PlanoDeGravacao.Visiveis} células", null, null));

        foreach (var g in grupos)
        {
            var ws = wb.Worksheet(g.Aba);
            int erros = g.Celulas.Count(x => Erro(ws.Cell(x.L, x.C)));
            string faixa = $"{Grade.Letra(g.C1)}{g.L1}:{Grade.Letra(g.C2)}{g.L2}";
            contagens.Add($"{faixa} · {g.Celulas.Count} células · {(g.Formula ? "fórmula" : "valor")}");

            int mostrar = g.Celulas.Count <= 3 || !resumir ? Math.Min(g.Celulas.Count, PlanoDeGravacao.Visiveis) : 3;
            if (g.Celulas.Count > mostrar)
                lista.Add(new Mudanca(TipoDeMudanca.Grupo,
                    $"{faixa} · {(g.Formula ? "a mesma fórmula" : "o mesmo valor")} em {g.Celulas.Count} células", null, null));

            foreach (var (l, c, ant) in g.Celulas.Take(mostrar))
            {
                var cel = ws.Cell(l, c);
                lista.Add(new Mudanca(TipoDeMudanca.Valor, Endereco(wb, g.Aba, l, c), ant, Mostrar(cel), Erro(cel)));
            }

            if (g.Celulas.Count > mostrar)
                lista.Add(new Mudanca(TipoDeMudanca.Mais,
                    $"+ {g.Celulas.Count - mostrar} células{(erros > 0 ? $" · {erros} dão erro" : "")}", null, null));
        }

        lista.AddRange(saem.Take(PlanoDeGravacao.Visiveis));
        if (saem.Count > PlanoDeGravacao.Visiveis) lista.Add(new Mudanca(TipoDeMudanca.Mais, $"+ {saem.Count - PlanoDeGravacao.Visiveis} linhas apagadas", null, null));
        lista.AddRange(entram.Take(PlanoDeGravacao.Visiveis));
        if (entram.Count > PlanoDeGravacao.Visiveis) lista.Add(new Mudanca(TipoDeMudanca.Mais, $"+ {entram.Count - PlanoDeGravacao.Visiveis} linhas inseridas", null, null));
        lista.AddRange(outras);

        var avisos = new List<string>();

        // Sem o tempo medido no texto: o plano é refeito na gravação e comparado com o do
        // cartão, e um número de segundos nunca sairia igual duas vezes.
        var motivos = new List<string>();
        if (total > CelulasDeEdicaoPesada) motivos.Add($"{total:N0} células de uma vez");
        if (antes.Count > FormulasDeEdicaoPesada) motivos.Add($"{antes.Count:N0} fórmulas recalculadas");
        bool pesada = motivos.Count > 0;
        if (pesada)
            avisos.Add($"EDIÇÃO PESADA E LENTA ({string.Join(", ", motivos)}). "
                       + "Preparar este cartão já demorou, e gravar vai demorar de novo — pode levar minutos.");
        foreach (var (onde, formula) in FormulasComRef(wb).Where(f => !refsAntes.ContainsKey(f.Key)).Take(5))
            avisos.Add($"A fórmula de {onde} vai dar #REF! (=" + Apresentacao.Curto(formula, 80) + "): ela apontava para uma linha apagada.");

        var efeitos = estrutural || formato == Formato.Csv ? new List<string>() : Efeitos(wb, antes, mudancas, grupos);

        string abaTexto = string.Join(", ", mudancas.Select(m => m.Aba).Concat(grupos.Select(g => g.Aba)).Distinct().Select(a => $"\"{a}\""));
        var partes = new List<string>();
        if (total > 0) partes.Add(total == 1 ? "1 célula" : $"{total} células");
        if (saem.Count > 0) partes.Add(saem.Count == 1 ? "apagar 1 linha" : $"apagar {saem.Count} linhas");
        if (entram.Count > 0) partes.Add(entram.Count == 1 ? "inserir 1 linha" : $"inserir {entram.Count} linhas");
        partes.AddRange(outras.Select(o => o.Onde));

        string titulo = saem.Count > 0 || entram.Count > 0
            ? Primeira(string.Join(" e ", partes.Where(p => !p.EndsWith("células") && !p.EndsWith("célula")))) + " nesta planilha?"
            : total > 0 ? $"Gravar {(total == 1 ? "1 célula" : $"{total} células")} nesta planilha?"
            : "Mudar esta planilha?";

        string consequencia = (pesada ? "Edição massiva: recalcular e gravar esta planilha é muito lento. " : "") + (saem.Count > 0 || entram.Count > 0
                ? "As linhas abaixo se deslocam. Fórmulas que apontam para linhas apagadas passam a dar #REF!. "
                : "Os valores atuais são substituídos. ")
            + (formato == Formato.Csv ? "" : "A planilha não pode estar aberta no Excel.");

        var plano = new PlanoDeGravacao
        {
            Arquivo = Path.GetFullPath(caminho),
            Criar = false,
            Titulo = titulo,
            Consequencia = consequencia.Trim(),
            Resumo = (abaTexto.Length > 0 ? $"aba {abaTexto} · " : "") + string.Join(" · ", partes),
            Contagens = resumir ? contagens : new List<string>(),
            Mudancas = lista,
            Efeitos = efeitos,
            Avisos = avisos,
            Copia = CopiasDeDocumento.Descricao(caminho),
            Impressao = impressao
        };

        return new GravacaoDePlanilha(plano, wb, csv, $"{string.Join(", ", partes)} gravado(s)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Criar
    // ─────────────────────────────────────────────────────────────────────────

    public static Gravacao PlanejarCriacao(string caminho, JsonElement conteudo)
    {
        var formato = Formatos.PelaExtensao(caminho);
        bool existe = File.Exists(caminho);
        if (existe) formato = Formatos.De(caminho);

        if (!conteudo.TryGetProperty("sheets", out var abas) || abas.ValueKind != JsonValueKind.Array || abas.GetArrayLength() == 0)
            throw new ErroDeDocumento("'content' de planilha precisa de 'sheets': [{name, rows:[[...]]}].");

        var wb = new XLWorkbook();
        int linhasTotal = 0, i = 0;
        var nomes = new List<string>();

        foreach (var a in abas.EnumerateArray())
        {
            i++;
            string nome = Texto(a, "name") is { Length: > 0 } n ? n : $"Planilha{i}";
            if (wb.Worksheets.Contains(nome)) throw new ErroDeDocumento($"duas abas com o nome \"{nome}\".");
            var ws = wb.AddWorksheet(nome);
            nomes.Add(nome);

            var linhas = Linhas(a, "rows", i);
            for (int r = 0; r < linhas.Count; r++)
                for (int c = 0; c < linhas[r].Count; c++)
                    Escrever(ws.Cell(r + 1, c + 1), linhas[r][c]);
            linhasTotal += linhas.Count;

            if (linhas.Count > 0 && formato != Formato.Csv)
            {
                ws.Row(1).Style.Font.Bold = true;
                ws.Columns(1, Math.Max(1, linhas.Max(l => l.Count))).AdjustToContents(1, Math.Min(linhas.Count, 200));
            }
        }

        if (formato == Formato.Csv && nomes.Count > 1)
            throw new ErroDeDocumento(".csv tem uma aba só. Para várias, use .xlsx.");

        Recalcular(wb);

        var primeira = wb.Worksheet(1);
        var usada = primeira.RangeUsed();
        var previa = new List<IEnumerable<string>>();
        if (usada != null)
        {
            int ultimaL = usada.LastRow().RowNumber(), ultimaC = Math.Min(usada.LastColumn().ColumnNumber(), 8);
            var indices = Enumerable.Range(1, ultimaL).ToList();
            foreach (int r in indices.Take(5).Concat(indices.Count > 5 ? new[] { ultimaL } : Array.Empty<int>()))
                previa.Add(Enumerable.Range(1, ultimaC).Select(c => Apresentacao.Curto(Mostrar(primeira.Cell(r, c)) ?? "", 30)));
        }

        int colunas = usada?.ColumnCount() ?? 0;
        var plano = new PlanoDeGravacao
        {
            Arquivo = Path.GetFullPath(caminho),
            Criar = !existe,
            Titulo = existe ? "Substituir esta planilha inteira?" : "Criar esta planilha?",
            Consequencia = existe
                ? "O arquivo existe: TODO o conteúdo atual é trocado por este."
                : "Arquivo novo; nada que já existe é tocado.",
            Resumo = $"{nomes.Count} aba(s) · {linhasTotal} linhas · {colunas} colunas",
            Previa = new PreviaDeArquivo(nomes, previa.Select(l => (IReadOnlyList<string>)l.ToList()).ToList(),
                                         Math.Max(0, (usada?.RowCount() ?? 0) - previa.Count)),
            Copia = existe ? CopiasDeDocumento.Descricao(caminho) : null,
            Impressao = existe ? Gravacao.Impressao(caminho) : null
        };

        return new GravacaoDePlanilha(plano, wb, formato == Formato.Csv ? new FormatoCsv(';', new UTF8Encoding(true)) : null,
                                      existe ? "planilha substituída" : "planilha criada");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // O que se perderia
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recusa editar o que o ClosedXML não sabe regravar: gráficos, tabela dinâmica, macros,
    /// segmentações. Ele abre o arquivo, mas grava SEM essas partes — a edição de uma célula
    /// apagaria o gráfico do usuário sem aviso.
    /// </summary>
    internal static void RecusarOQueSePerderia(string caminho, Formato formato)
    {
        if (formato != Formato.Xlsx) return;

        var perderia = new List<string>();
        try
        {
            using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            bool tem(string prefixo) => zip.Entries.Any(e => e.FullName.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase));

            if (tem("xl/charts/")) perderia.Add("gráficos");
            if (tem("xl/pivotTables/") || tem("xl/pivotCache/")) perderia.Add("tabelas dinâmicas");
            if (tem("xl/vbaProject")) perderia.Add("macros");
            if (tem("xl/slicers/") || tem("xl/slicerCaches/")) perderia.Add("segmentações");
            if (tem("xl/timelines/")) perderia.Add("linhas do tempo");
        }
        catch (InvalidDataException)
        {
            throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' não é um .xlsx válido.");
        }

        if (perderia.Count > 0)
            throw new ErroDeDocumento(
                $"'{Path.GetFileName(caminho)}' tem {string.Join(", ", perderia)}, que esta ferramenta não sabe regravar: "
                + "editar o arquivo os apagaria. Nada foi gravado. Ofereça criar um arquivo novo com o resultado "
                + "(action='create'), ou peça ao usuário para fazer a mudança no Excel.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Abrir e gravar
    // ─────────────────────────────────────────────────────────────────────────

    private static (XLWorkbook, FormatoCsv?) Abrir(string caminho, Formato formato)
    {
        if (formato == Formato.Csv)
        {
            var (grade, separador, codificacao) = LeitorDeCsv.Ler(caminho);
            var wb = new XLWorkbook();
            var ws = wb.AddWorksheet(SemCaracteresProibidos(Path.GetFileNameWithoutExtension(caminho)));
            for (int r = grade.PrimeiraLinha; r <= grade.Linhas; r++)
                for (int c = 1; c <= grade.Colunas; c++)
                    if (grade[r, c].Texto is { Length: > 0 } t) ws.Cell(r, c).Value = t;
            return (wb, new FormatoCsv(separador, codificacao));
        }

        if (formato != Formato.Xlsx) throw new ErroDeDocumento("só .xlsx, .xlsm e .csv se editam.");

        using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var mem = new MemoryStream();
        fs.CopyTo(mem);
        mem.Position = 0;
        return (new XLWorkbook(mem), null);
    }

    private static string SemCaracteresProibidos(string nome)
    {
        var limpo = new string(nome.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        return limpo.Length == 0 ? "Planilha1" : Apresentacao.Curto(limpo, 31).TrimEnd('…');
    }

    internal sealed record FormatoCsv(char Separador, Encoding Codificacao);

    private sealed class GravacaoDePlanilha : Gravacao
    {
        private readonly XLWorkbook _wb;
        private readonly FormatoCsv? _csv;

        public GravacaoDePlanilha(PlanoDeGravacao plano, XLWorkbook wb, FormatoCsv? csv, string resultado) : base(plano)
        {
            _wb = wb;
            _csv = csv;
            Resultado = resultado;
        }

        public override string Resultado { get; }

        protected override void GravarEm(string caminho)
        {
            if (_csv == null)
            {
                _wb.SaveAs(caminho);
                return;
            }

            var ws = _wb.Worksheet(1);
            var usada = ws.RangeUsed();
            var sb = new StringBuilder();
            if (usada != null)
            {
                int ultimaL = usada.LastRow().RowNumber(), ultimaC = usada.LastColumn().ColumnNumber();
                for (int r = 1; r <= ultimaL; r++)
                {
                    var celulas = Enumerable.Range(1, ultimaC).Select(c => Csv(ParaCsv(ws.Cell(r, c)), _csv.Separador));
                    sb.Append(string.Join(_csv.Separador, celulas).TrimEnd(_csv.Separador)).Append("\r\n");
                }
            }
            File.WriteAllText(caminho, sb.ToString(), _csv.Codificacao);
        }

        /// <summary>
        /// O texto que volta ao .csv: o que foi lido como texto volta igual; número e data no
        /// padrão do separador (pt-BR com ';', invariante com ',').
        /// </summary>
        private string ParaCsv(IXLCell c)
        {
            var v = c.Value;
            var cultura = _csv!.Separador == ',' ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("pt-BR");
            if (v.IsNumber) return v.GetNumber().ToString("0.##########", cultura);
            if (v.IsDateTime) return v.GetDateTime().TimeOfDay == TimeSpan.Zero
                ? v.GetDateTime().ToString(_csv.Separador == ',' ? "yyyy-MM-dd" : "dd/MM/yyyy", cultura)
                : v.GetDateTime().ToString(_csv.Separador == ',' ? "yyyy-MM-dd HH:mm" : "dd/MM/yyyy HH:mm", cultura);
            if (v.IsBoolean) return v.GetBoolean() ? "VERDADEIRO" : "FALSO";
            return v.IsBlank ? "" : v.ToString();
        }

        private static string Csv(string valor, char separador) =>
            valor.IndexOfAny(new[] { separador, '"', '\n', '\r' }) >= 0 ? "\"" + valor.Replace("\"", "\"\"") + "\"" : valor;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Células e valores
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class Grupo
    {
        public Grupo(string aba, int c1, int c2, int l1, int l2, bool formula)
        {
            Aba = aba; C1 = c1; C2 = c2; L1 = l1; L2 = l2; Formula = formula;
        }
        public string Aba { get; }
        public int C1 { get; }
        public int C2 { get; }
        public int L1 { get; }
        public int L2 { get; }
        public bool Formula { get; }
        public List<(int L, int C, string? Antes)> Celulas { get; } = new();
    }

    /// <summary>
    /// O valor de uma célula como o cartão mostra: o texto formatado; fórmula como
    /// "=C15*12 → 7.680,00". Null se vazia.
    /// </summary>
    internal static string? Mostrar(IXLCell c)
    {
        if (c.HasFormula)
        {
            string valor;
            try { valor = c.CachedValue.IsBlank ? "" : Formatado(c); }
            catch { valor = "?"; }
            return $"={c.FormulaA1} → {valor}";
        }
        string t = Formatado(c);
        return t.Length == 0 ? null : Apresentacao.Curto(Apresentacao.Achatar(t), 120);
    }

    private static string Formatado(IXLCell c)
    {
        try { return LeitorDePlanilha.Texto(c); }
        catch { return c.CachedValue.ToString(); }
    }

    private static bool Erro(IXLCell c)
    {
        try { return c.HasFormula ? c.CachedValue.IsError : c.Value.IsError; }
        catch { return false; }
    }

    /// <summary>
    /// Escreve um valor vindo do JSON: texto com "=" é fórmula; número, booleano e null como
    /// são; texto que parece data ISO (2026-10-19) vira data.
    /// </summary>
    internal static void Escrever(IXLCell c, object? valor)
    {
        switch (valor)
        {
            case null:
                c.Clear(XLClearOptions.Contents);
                break;
            case string s when s.StartsWith('=') && s.Length > 1:
                c.FormulaA1 = s[1..];
                break;
            case string s when DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss" },
                                                      CultureInfo.InvariantCulture, DateTimeStyles.None, out var data):
                c.Value = data;
                break;
            case string s:
                c.Value = s;
                break;
            case double d:
                c.Value = d;
                break;
            case bool b:
                c.Value = b;
                break;
            default:
                c.Value = valor.ToString();
                break;
        }
    }

    private static object? Valor(JsonElement op, int i)
    {
        if (!op.TryGetProperty("value", out var v)) throw new ErroDeDocumento($"operação {i}: falta 'value' (null apaga a célula).");
        return DoJson(v);
    }

    private static object? DoJson(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => v.GetRawText()
    };

    private static List<List<object?>> Linhas(JsonElement pai, string nome, int i)
    {
        if (!pai.TryGetProperty(nome, out var linhas) || linhas.ValueKind != JsonValueKind.Array)
            return new List<List<object?>>();

        var lista = new List<List<object?>>();
        foreach (var l in linhas.EnumerateArray())
        {
            if (l.ValueKind != JsonValueKind.Array) throw new ErroDeDocumento($"operação {i}: cada linha de '{nome}' é uma lista de valores.");
            lista.Add(l.EnumerateArray().Select(DoJson).ToList());
            if (lista.Count * Math.Max(1, lista[^1].Count) > TetoDeCelulas) throw new ErroDeDocumento($"passa de {TetoDeCelulas} células.");
        }
        return lista;
    }

    private static (int, int) LinhasDe(JsonElement op, int i)
    {
        var v = op.GetProperty("delete_rows");
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int unica) && unica >= 1) return (unica, unica);

        string t = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var partes = t.Split(':', '-');
        if (partes.Length is 1 or 2 && int.TryParse(partes[0], out int de) && de >= 1)
        {
            int ate = partes.Length == 2 && int.TryParse(partes[1], out int a) ? a : de;
            if (ate < de) (de, ate) = (ate, de);
            return (de, ate);
        }
        throw new ErroDeDocumento($"operação {i}: 'delete_rows' é '18:19' ou um número.");
    }

    private static IXLWorksheet Aba(XLWorkbook wb, string? nome, int i)
    {
        if (string.IsNullOrWhiteSpace(nome))
            return wb.Worksheets.FirstOrDefault(w => w.Visibility == XLWorksheetVisibility.Visible) ?? wb.Worksheet(1);

        return wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, nome.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new ErroDeDocumento($"operação {i}: não há aba \"{nome}\". Abas: {string.Join(", ", wb.Worksheets.Select(w => $"\"{w.Name}\""))}.");
    }

    private static IXLCell Celula(IXLWorksheet ws, string endereco, int i)
    {
        var (l, c) = Apresentacao.Ponto(endereco.Replace("$", ""), endereco);
        if (l is null || c is null) throw new ErroDeDocumento($"operação {i}: '{endereco}' não é uma célula (ex.: 'B12').");
        return ws.Cell(l.Value, c.Value);
    }

    private static IXLRange Faixa(IXLWorksheet ws, string faixa, int i)
    {
        var partes = faixa.Replace("$", "").Split(':');
        if (partes.Length != 2) throw new ErroDeDocumento($"operação {i}: 'range' é 'D2:D50'.");
        var (l1, c1) = Apresentacao.Ponto(partes[0], faixa);
        var (l2, c2) = Apresentacao.Ponto(partes[1], faixa);
        if (l1 is null || c1 is null || l2 is null || c2 is null)
            throw new ErroDeDocumento($"operação {i}: 'range' precisa de células nas duas pontas ('D2:D50').");
        if ((long)(Math.Abs(l2.Value - l1.Value) + 1) * (Math.Abs(c2.Value - c1.Value) + 1) > TetoDeCelulas)
            throw new ErroDeDocumento($"operação {i}: a faixa passa de {TetoDeCelulas} células.");
        return ws.Range(l1.Value, c1.Value, l2.Value, c2.Value);
    }

    private static string Endereco(XLWorkbook wb, string aba, int l, int c) =>
        wb.Worksheets.Count == 1 ? $"{Grade.Letra(c)}{l}" : $"{aba}!{Grade.Letra(c)}{l}";

    private static string Primeira(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    private static string? Texto(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static readonly Regex Referencia = new(
        @"(?<![\p{L}0-9_.!$])(?:(?:'(?<aba>[^']+)'|(?<aba>[\p{L}0-9_.]+))!)?\$?(?<c1>[A-Za-z]{1,3})\$?(?<l1>\d+)(?::\$?(?<c2>[A-Za-z]{1,3})\$?(?<l2>\d+))?(?![A-Za-z0-9_(])",
        RegexOptions.Compiled);

    /// <summary>
    /// Antes de apagar linhas, troca por #REF! a referência que aponta SÓ para elas — como o
    /// Excel faz.
    /// <para>
    /// O ClosedXML não faz: visto no ensaio de 19/10/2026, com a linha 3 apagada, "=B3*2"
    /// virou "=B2*2" e "=Ramais!B3" virou "=Ramais!B2". A fórmula passava a ler OUTRO dado, em
    /// silêncio. Faixa que só encosta nas linhas apagadas ("SUM(B2:B4)") ele encolhe certo, e
    /// fica como está.
    /// </para>
    /// </summary>
    private static void AnularReferencias(XLWorkbook wb, IXLWorksheet alvo, int de, int ate)
    {
        foreach (var c in CelulasComFormula(wb).ToList())
        {
            string formula = c.FormulaA1;
            var partes = formula.Split('"');
            bool mudou = false;

            // As partes ímpares estão entre aspas: texto, não referência.
            for (int i = 0; i < partes.Length; i += 2)
            {
                partes[i] = Referencia.Replace(partes[i], m =>
                {
                    string aba = m.Groups["aba"].Success ? m.Groups["aba"].Value : c.Worksheet.Name;
                    if (!string.Equals(aba, alvo.Name, StringComparison.OrdinalIgnoreCase)) return m.Value;

                    int l1 = int.Parse(m.Groups["l1"].Value, CultureInfo.InvariantCulture);
                    int l2 = m.Groups["l2"].Success ? int.Parse(m.Groups["l2"].Value, CultureInfo.InvariantCulture) : l1;
                    if (Math.Min(l1, l2) < de || Math.Max(l1, l2) > ate) return m.Value;

                    mudou = true;
                    return "#REF!";
                });
            }

            if (mudou) c.FormulaA1 = string.Join("\"", partes);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fórmulas: recalcular e o efeito
    // ─────────────────────────────────────────────────────────────────────────

    private static void Recalcular(XLWorkbook wb)
    {
        // Função que o ClosedXML não conhece não derruba a edição: a célula fica com o valor
        // em cache, e o Excel recalcula ao abrir.
        try { wb.RecalculateAllFormulas(); }
        catch { }
    }

    private static IEnumerable<IXLCell> CelulasComFormula(XLWorkbook wb) =>
        wb.Worksheets.SelectMany(w => w.CellsUsed(XLCellsUsedOptions.Contents).Where(c => c.HasFormula));

    /// <summary>O valor de cada fórmula que já existia, por endereço completo.</summary>
    private static Dictionary<string, string> ValoresDasFormulas(XLWorkbook wb)
    {
        var mapa = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in CelulasComFormula(wb))
            mapa[$"{c.Worksheet.Name}!{c.Address}"] = Formatado(c);
        return mapa;
    }

    /// <summary>Fórmulas que contêm #REF! — para saber quais passaram a ter depois da edição.</summary>
    private static Dictionary<string, string> FormulasComRef(XLWorkbook wb)
    {
        var mapa = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in CelulasComFormula(wb))
            if (c.FormulaA1.Contains("#REF!", StringComparison.Ordinal))
                mapa[$"{c.Worksheet.Name}!{c.Address}"] = c.FormulaA1;
        return mapa;
    }

    /// <summary>
    /// As fórmulas que JÁ existiam e mudam de valor por causa da edição — o que um antes e
    /// depois de células sozinho esconde ("o total do mês vai de 12.400 para 13.150").
    /// </summary>
    private static List<string> Efeitos(XLWorkbook wb, Dictionary<string, string> antes,
                                        List<(string Aba, int Linha, int Coluna, string? Antes)> mudancas, List<Grupo> grupos)
    {
        var efeitos = new List<string>();
        if (antes.Count == 0) return efeitos;

        var tocadas = new HashSet<string>(mudancas.Select(m => $"{m.Aba}!{Grade.Letra(m.Coluna)}{m.Linha}")
            .Concat(grupos.SelectMany(g => g.Celulas.Select(x => $"{g.Aba}!{Grade.Letra(x.C)}{x.L}"))), StringComparer.Ordinal);

        foreach (var c in CelulasComFormula(wb))
        {
            string chave = $"{c.Worksheet.Name}!{c.Address}";
            if (tocadas.Contains(chave) || !antes.TryGetValue(chave, out var velho)) continue;

            string novo = Formatado(c);
            if (novo == velho) continue;

            string rotulo = Rotulo(c);
            string onde = wb.Worksheets.Count == 1 ? c.Address.ToString()! : chave;
            efeitos.Add($"{onde}{(rotulo.Length > 0 ? $" ({rotulo})" : "")}: {velho} → {novo}");
            if (efeitos.Count >= 6) break;
        }
        return efeitos;
    }

    /// <summary>O primeiro texto à esquerda na mesma linha: "Total", "Acumulado".</summary>
    private static string Rotulo(IXLCell c)
    {
        for (int col = c.Address.ColumnNumber - 1; col >= 1; col--)
        {
            var v = c.Worksheet.Cell(c.Address.RowNumber, col);
            if (!v.HasFormula && v.DataType == XLDataType.Text && v.GetString().Trim() is { Length: > 0 } t)
                return Apresentacao.Curto(t, 30);
        }
        return "";
    }
}
