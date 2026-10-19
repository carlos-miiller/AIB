using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIB.Services.Documentos;

/// <summary>
/// A ação <c>query</c>: filtrar, agrupar e somar uma aba em C#.
/// <para>
/// O modelo lia 240 linhas e somava de cabeça — errava a soma, pulava linha e não avisava que
/// "n/d" no meio da coluna não entrou na conta. Aqui a conta é exata e a saída diz o que ficou
/// de fora.
/// </para>
/// </summary>
public static class ConsultaDePlanilha
{
    public const int LimitePadrao = 50;
    public const int LimiteMaximo = 500;

    private const string Exemplo =
        "query = {\"where\":[{\"col\":\"Setor\",\"op\":\"=\",\"value\":\"TI\"}],\"group\":\"Setor\","
        + "\"agg\":[{\"fn\":\"sum\",\"col\":\"Horas\"}],\"sort\":\"-sum(Horas)\",\"limit\":20} — tudo opcional";

    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] Chaves = { "where", "group", "agg", "sort", "limit" };
    private static readonly string[] Funcoes = { "sum", "count", "avg", "min", "max" };

    // "1.234,56", "12,5", "1234" (pt-BR) e "1234.56" (invariante). "1.234" é milhar, como no Excel brasileiro.
    private static readonly Regex NumeroBr = new(@"^[-+]?(\d{1,3}(\.\d{3})+|\d+)(,\d+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex NumeroInv = new(@"^[-+]?\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex AggEmTexto = new(@"^\s*(\w+)\s*(?:\((.*)\))?\s*$", RegexOptions.CultureInvariant);
    private static readonly string[] Datas = { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-ddTHH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss" };

    private sealed record Coluna(int Numero, string Nome);

    private sealed record Filtro(Coluna Col, string Op, string Valor, double? Numero, DateTime? Data, bool? Booleano);

    private sealed record Agregacao(string Fn, Coluna? Col)
    {
        public string Rotulo => Col == null ? Fn : $"{Fn}({Col.Nome})";
    }

    private sealed record Resultado(string Texto, Chave Chave, int Ignoradas);

    /// <summary>Ordem: número (e data) antes de texto, vazio sempre no fim, nos dois sentidos.</summary>
    private readonly record struct Chave(int Classe, double Numero, string Texto)
    {
        public static readonly Chave Vazia = new(2, 0, "");

        public static int Comparar(Chave a, Chave b, bool desc)
        {
            if (a.Classe != b.Classe) return a.Classe.CompareTo(b.Classe);
            int cmp = a.Classe == 0 ? a.Numero.CompareTo(b.Numero) : string.CompareOrdinal(a.Texto, b.Texto);
            return desc ? -cmp : cmp;
        }
    }

    private sealed class Ordem : IComparer<Chave>
    {
        private readonly bool _desc;
        public Ordem(bool desc) => _desc = desc;
        public int Compare(Chave a, Chave b) => Chave.Comparar(a, b, _desc);
    }

    public static string Executar(Pasta pasta, string? aba, JsonElement consulta, int teto)
    {
        var grade = Apresentacao.AbaOuErro(pasta, aba);
        if (Apresentacao.Cabecalho(grade) is not int cab)
            throw new ErroDeDocumento($"a aba \"{grade.Nome}\" não tem cabeçalho nas 5 primeiras linhas (está vazia no alto?). "
                                      + "Veja o conteúdo com action='read'.");

        var colunas = Enumerable.Range(1, grade.Colunas).Select(c => new Coluna(c, Nome(grade, cab, c))).ToList();
        var pedido = Objeto(consulta);

        var filtros = new List<Filtro>();
        Coluna? grupo = null;
        var aggs = new List<Agregacao>();
        string? sort = null;
        int limite = LimitePadrao;

        foreach (var p in pedido.EnumerateObject())
        {
            switch (p.Name.Trim().ToLowerInvariant())
            {
                case "where": filtros.AddRange(Itens(p.Value).Select(i => LerFiltro(i, grade, cab, colunas))); break;
                case "group": grupo = p.Value.ValueKind is JsonValueKind.Null ? null : Achar(Texto(p.Value, "group"), grade, cab, colunas); break;
                case "agg": aggs.AddRange(Itens(p.Value).Select(i => LerAgg(i, grade, cab, colunas))); break;
                case "sort": sort = p.Value.ValueKind is JsonValueKind.Null ? null : Texto(p.Value, "sort"); break;
                case "limit": limite = Limite(p.Value); break;
                default:
                    throw new ErroDeDocumento($"a consulta não conhece \"{p.Name}\". Chaves: {string.Join(", ", Chaves)}. Exemplo: {Exemplo}.");
            }
        }

        if (grupo != null && aggs.Count == 0) aggs.Add(new Agregacao("count", null));

        var dados = new List<int>();
        for (int r = cab + 1; r <= grade.Linhas; r++)
            if (!grade.LinhaVazia(r)) dados.Add(r);
        var linhas = dados.Where(r => filtros.All(f => Passa(grade, r, f))).ToList();

        string topo = $"Aba \"{grade.Nome}\" · {Plural(dados.Count, "linha", "linhas")} de dados (cabeçalho na linha {cab})";
        if (filtros.Count > 0)
            topo += $" · filtro: {string.Join(", ", filtros.Select(Descrever))} → {Plural(linhas.Count, "linha", "linhas")}";

        if (grupo != null) return Grupos(grade, topo, linhas, grupo, aggs, sort, limite, colunas, cab, teto);
        if (aggs.Count > 0) return Totais(grade, topo, linhas, aggs, teto);
        return Linhas(grade, topo, linhas, colunas, cab, sort, limite, teto);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Saídas
    // ─────────────────────────────────────────────────────────────────────────

    private static string Linhas(Grade grade, string topo, List<int> linhas, List<Coluna> colunas, int cab,
                                 string? sort, int limite, int teto)
    {
        if (sort != null)
        {
            var (s, desc) = Sentido(sort);
            var col = Achar(s, grade, cab, colunas);
            linhas = linhas.OrderBy(r => ChaveDe(grade[r, col.Numero]), new Ordem(desc)).ToList();
        }

        string cabecalho = "linha\t" + string.Join("\t", colunas.Select(c => c.Nome));
        var itens = linhas.Take(limite).Select(r =>
            (r + "\t" + string.Join("\t", colunas.Select(c => Apresentacao.Curto(Apresentacao.Achatar(grade[r, c.Numero].Texto), 300))))
            .TrimEnd('\t')).ToList();

        return Montar(topo, cabecalho, itens, linhas.Count, "linhas", new List<string>(), teto);
    }

    private static string Totais(Grade grade, string topo, List<int> linhas, List<Agregacao> aggs, int teto)
    {
        var res = aggs.Select(a => Calcular(a, grade, linhas)).ToList();
        var itens = aggs.Select((a, i) => $"{a.Rotulo}\t{res[i].Texto}").ToList();
        return Montar(topo, null, itens, itens.Count, "agregações", Notas(aggs, res), teto);
    }

    private static string Grupos(Grade grade, string topo, List<int> linhas, Coluna grupo, List<Agregacao> aggs,
                                 string? sort, int limite, List<Coluna> colunas, int cab, int teto)
    {
        // Na ordem em que cada valor aparece primeiro: é a ordem que o usuário vê na planilha.
        var ordem = new List<(string Chave, List<int> Linhas)>();
        var indice = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (int r in linhas)
        {
            string k = Apresentacao.Achatar(grade[r, grupo.Numero].Texto.Trim());
            if (k.Length == 0) k = "(vazio)";
            if (!indice.TryGetValue(k, out int i))
            {
                indice[k] = i = ordem.Count;
                ordem.Add((k, new List<int>()));
            }
            ordem[i].Linhas.Add(r);
        }

        var grupos = ordem.Select(g => (g.Chave, Res: aggs.Select(a => Calcular(a, grade, g.Linhas)).ToList())).ToList();

        if (sort != null)
        {
            var (s, desc) = Sentido(sort);
            int alvo = AlvoDoSort(s, aggs, grupo, grade, cab, colunas);
            grupos = grupos.OrderBy(g => alvo < 0 ? ChaveDe(g.Chave == "(vazio)" ? "" : g.Chave) : g.Res[alvo].Chave, new Ordem(desc)).ToList();
        }

        string cabecalho = grupo.Nome + "\t" + string.Join("\t", aggs.Select(a => a.Rotulo));
        var itens = grupos.Take(limite)
            .Select(g => Apresentacao.Curto(g.Chave, 80) + "\t" + string.Join("\t", g.Res.Select(x => x.Texto)))
            .ToList();

        var total = aggs.Select(a => Calcular(a, grade, linhas)).ToList();
        var depois = new List<string> { "TOTAL\t" + string.Join("\t", total.Select(x => x.Texto)) };
        depois.AddRange(Notas(aggs, total));

        return Montar(topo, cabecalho, itens, grupos.Count, "grupos", depois, teto);
    }

    /// <summary>Índice da agregação pela qual ordenar, ou -1 para ordenar pelo próprio grupo.</summary>
    private static int AlvoDoSort(string s, List<Agregacao> aggs, Coluna grupo, Grade grade, int cab, List<Coluna> colunas)
    {
        string alvo = Compacto(s);
        int i = aggs.FindIndex(a => Compacto(a.Rotulo) == alvo
                                    || (a.Col != null && Compacto($"{a.Fn}({Grade.Letra(a.Col.Numero)})") == alvo));
        if (i >= 0) return i;

        var (col, _) = Tentar(s, grade, cab, colunas);
        if (col != null && col.Numero == grupo.Numero) return -1;
        if (col != null && aggs.Count(a => a.Col?.Numero == col.Numero) == 1)
            return aggs.FindIndex(a => a.Col?.Numero == col.Numero);

        throw new ErroDeDocumento($"não sei ordenar por \"{s}\". Use \"{grupo.Nome}\" ou o rótulo de uma agregação: "
                                  + $"{string.Join(", ", aggs.Select(a => $"\"{a.Rotulo}\""))} (com \"-\" na frente para decrescente).");
    }

    private static List<string> Notas(List<Agregacao> aggs, List<Resultado> res) =>
        aggs.Select((a, i) => res[i].Ignoradas == 0 || a.Col == null ? null
                : $"{Plural(res[i].Ignoradas, "célula não numérica ignorada", "células não numéricas ignoradas")} em {a.Col.Nome}")
            .OfType<string>()
            .Distinct()
            .ToList();

    /// <summary>
    /// Junta topo, cabeçalho, itens e rodapé sem passar do teto. O rodapé (TOTAL, notas) tem
    /// lugar reservado: cortar a soma para caber mais uma linha seria esconder o principal.
    /// </summary>
    private static string Montar(string topo, string? cabecalho, List<string> itens, int total, string unidade,
                                 List<string> depois, int teto)
    {
        var sb = new StringBuilder(topo).Append('\n');
        if (cabecalho != null) sb.Append(cabecalho).Append('\n');

        int reserva = depois.Sum(l => l.Length + 1) + 90;
        int mostradas = 0;
        foreach (var item in itens)
        {
            if (sb.Length + item.Length + 1 + reserva > teto) break;
            sb.Append(item).Append('\n');
            mostradas++;
        }

        if (mostradas < total)
        {
            string dica = mostradas < itens.Count
                ? " — a saída chegou ao tamanho máximo; filtre mais"
                : itens.Count < LimiteMaximo ? $" — aumente limit (até {LimiteMaximo}), filtre ou ordene" : " — filtre ou ordene";
            sb.Append($"[{total} {unidade}; mostrando {mostradas}{dica}]\n");
        }

        foreach (var l in depois) sb.Append(l).Append('\n');

        string r = sb.ToString().TrimEnd();
        return r.Length <= teto ? r : Apresentacao.Curto(r, Math.Max(2, teto));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Contas
    // ─────────────────────────────────────────────────────────────────────────

    private static Resultado Calcular(Agregacao a, Grade grade, List<int> linhas)
    {
        if (a.Fn == "count")
        {
            int n = a.Col == null ? linhas.Count : linhas.Count(r => !Vazia(grade[r, a.Col.Numero]));
            return new Resultado(n.ToString(Br), new Chave(0, n, ""), 0);
        }

        var nums = new List<double>();
        var datas = new List<DateTime>();
        int ignoradas = 0;
        foreach (int r in linhas)
        {
            var cel = grade[r, a.Col!.Numero];
            if (Vazia(cel)) continue;
            if (NumeroDe(cel) is double v) nums.Add(v);
            else if (a.Fn is "min" or "max" && DataDe(cel) is DateTime d) datas.Add(d);
            else ignoradas++;
        }

        if (nums.Count == 0 && datas.Count == 0) return new Resultado("—", Chave.Vazia, ignoradas);

        if (nums.Count == 0)
        {
            var d = a.Fn == "min" ? datas.Min() : datas.Max();
            return new Resultado(d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), new Chave(0, d.ToOADate(), ""), ignoradas);
        }

        // Datas no meio de números não entram em min/max numérico: contam como ignoradas.
        ignoradas += datas.Count;
        double x = a.Fn switch
        {
            "sum" => nums.Sum(),
            "avg" => nums.Average(),
            "min" => nums.Min(),
            _ => nums.Max()
        };
        string texto = a.Fn == "avg" ? Math.Round(x, 2).ToString("#,##0.##", Br) : Mostrar(x);
        return new Resultado(texto, new Chave(0, x, ""), ignoradas);
    }

    private static bool Passa(Grade grade, int r, Filtro f)
    {
        var cel = grade[r, f.Col.Numero];
        string texto = Apresentacao.Normalizar(cel.Texto.Trim());
        string valor = Apresentacao.Normalizar(f.Valor.Trim());
        if (f.Op == "contains") return texto.Contains(valor, StringComparison.Ordinal);

        int? cmp = null;
        bool tipado = false;
        if (f.Numero is double n)
        {
            tipado = true;
            if (NumeroDe(cel) is double v) cmp = v.CompareTo(n);
        }
        if (cmp == null && f.Data is DateTime fd)
        {
            tipado = true;
            if (DataDe(cel) is DateTime d) cmp = d.Date.CompareTo(fd.Date);
        }
        if (cmp == null && f.Booleano is bool fb && cel.Valor is bool b) cmp = b.CompareTo(fb);

        if (cmp == null)
        {
            // "Horas > 2" numa célula "n/d": comparar texto com número daria resultado sem sentido.
            if (tipado && f.Op is not ("=" or "!=")) return false;
            cmp = string.CompareOrdinal(texto, valor);
        }

        return f.Op switch
        {
            "=" => cmp == 0,
            "!=" => cmp != 0,
            ">" => cmp > 0,
            "<" => cmp < 0,
            ">=" => cmp >= 0,
            _ => cmp <= 0
        };
    }

    private static bool Vazia(Celula c) =>
        string.IsNullOrWhiteSpace(c.Texto) && (c.Valor is null || c.Valor is string s && string.IsNullOrWhiteSpace(s));

    private static double? NumeroDe(Celula c) => c.Valor switch
    {
        double d => d,
        int or long or float or decimal or short or byte => Convert.ToDouble(c.Valor, CultureInfo.InvariantCulture),
        string s => NumeroDe(s),
        null => NumeroDe(c.Texto),
        _ => null
    };

    private static double? NumeroDe(string texto)
    {
        string t = texto.Trim().Replace(" ", "").Replace(" ", "");
        if (t.Length == 0) return null;
        if (NumeroBr.IsMatch(t))
            return double.Parse(t.Replace(".", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (NumeroInv.IsMatch(t))
            return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        return null;
    }

    private static DateTime? DataDe(Celula c) => c.Valor switch
    {
        DateTime d => d,
        string s => DataDe(s),
        null => DataDe(c.Texto),
        _ => null
    };

    private static DateTime? DataDe(string texto) =>
        DateTime.TryParseExact(texto.Trim(), Datas, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static Chave ChaveDe(Celula c)
    {
        if (Vazia(c)) return Chave.Vazia;
        if (NumeroDe(c) is double n) return new Chave(0, n, "");
        if (DataDe(c) is DateTime d) return new Chave(0, d.ToOADate(), "");
        return new Chave(1, 0, Apresentacao.Normalizar(c.Texto.Trim()));
    }

    private static Chave ChaveDe(string texto) => ChaveDe(new Celula(texto));

    private static string Mostrar(double x) => Math.Round(x, 6).ToString("#,##0.######", Br);

    // ─────────────────────────────────────────────────────────────────────────
    // Pedido
    // ─────────────────────────────────────────────────────────────────────────

    private static JsonElement Objeto(JsonElement consulta)
    {
        switch (consulta.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return JsonDocument.Parse("{}").RootElement;
            case JsonValueKind.Object:
                return consulta;
            case JsonValueKind.String when consulta.GetString() is { } s && s.TrimStart().StartsWith('{'):
                // Há modelo que manda o objeto como texto JSON.
                try { return Objeto(JsonDocument.Parse(s).RootElement.Clone()); }
                catch (JsonException) { break; }
        }
        throw new ErroDeDocumento($"'query' deve ser um objeto. Exemplo: {Exemplo}.");
    }

    private static IEnumerable<JsonElement> Itens(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Array => e.EnumerateArray().ToList(),
        JsonValueKind.Null => Array.Empty<JsonElement>(),
        _ => new[] { e }
    };

    private static Filtro LerFiltro(JsonElement e, Grade grade, int cab, List<Coluna> colunas)
    {
        if (e.ValueKind != JsonValueKind.Object || !Prop(e, "col", out var col))
            throw new ErroDeDocumento("cada item de 'where' é {\"col\":\"Setor\",\"op\":\"=\",\"value\":\"TI\"}; "
                                      + "op: =, !=, >, <, >=, <=, contains.");

        string op = Prop(e, "op", out var o) ? Texto(o, "op").Trim().ToLowerInvariant() : "=";
        op = op switch
        {
            "==" => "=",
            "<>" => "!=",
            "contem" or "contém" or "like" => "contains",
            _ => op
        };
        if (op is not ("=" or "!=" or ">" or "<" or ">=" or "<=" or "contains"))
            throw new ErroDeDocumento($"op \"{op}\" desconhecido em 'where'. Use =, !=, >, <, >=, <= ou contains.");

        string valor = "";
        double? numero = null;
        DateTime? data = null;
        bool? booleano = null;
        if (Prop(e, "value", out var v))
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    valor = v.GetRawText();
                    numero = v.GetDouble();
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    booleano = v.GetBoolean();
                    valor = booleano.Value ? "true" : "false";
                    break;
                case JsonValueKind.String:
                    valor = v.GetString() ?? "";
                    numero = NumeroDe(valor);
                    data = numero == null ? DataDe(valor) : null;
                    break;
                case JsonValueKind.Null:
                    break;
                default:
                    throw new ErroDeDocumento("'value' em 'where' é texto, número ou true/false.");
            }
        }

        return new Filtro(Achar(Texto(col, "col"), grade, cab, colunas), op, valor, numero, data, booleano);
    }

    private static Agregacao LerAgg(JsonElement e, Grade grade, int cab, List<Coluna> colunas)
    {
        string? fn = null, col = null;
        if (e.ValueKind == JsonValueKind.String && AggEmTexto.Match(e.GetString() ?? "") is { Success: true } m)
        {
            fn = m.Groups[1].Value;
            col = m.Groups[2].Success && m.Groups[2].Value.Trim().Length > 0 ? m.Groups[2].Value : null;
        }
        else if (e.ValueKind == JsonValueKind.Object && Prop(e, "fn", out var f))
        {
            fn = Texto(f, "fn");
            col = Prop(e, "col", out var c) && c.ValueKind != JsonValueKind.Null ? Texto(c, "col") : null;
        }

        fn = fn?.Trim().ToLowerInvariant();
        if (fn == "average" || fn == "media" || fn == "média") fn = "avg";
        if (fn == null || !Funcoes.Contains(fn))
            throw new ErroDeDocumento($"cada item de 'agg' é {{\"fn\":\"sum\",\"col\":\"Horas\"}}; fn: {string.Join(", ", Funcoes)} "
                                      + "(count sem col conta linhas).");
        if (col == null && fn != "count")
            throw new ErroDeDocumento($"'{fn}' precisa de col: {{\"fn\":\"{fn}\",\"col\":\"Horas\"}}.");

        return new Agregacao(fn, col == null ? null : Achar(col, grade, cab, colunas));
    }

    private static int Limite(JsonElement e)
    {
        int n = e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetDouble(out double d) => (int)Math.Min(int.MaxValue, Math.Max(0, d)),
            JsonValueKind.String when int.TryParse(e.GetString(), out int i) => i,
            JsonValueKind.Null => LimitePadrao,
            _ => throw new ErroDeDocumento($"'limit' é um número (padrão {LimitePadrao}, máximo {LimiteMaximo}).")
        };
        return Math.Clamp(n, 1, LimiteMaximo);
    }

    private static bool Prop(JsonElement e, string nome, out JsonElement valor)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name.Trim(), nome, StringComparison.OrdinalIgnoreCase))
            {
                valor = p.Value;
                return true;
            }
        valor = default;
        return false;
    }

    private static string Texto(JsonElement e, string chave) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetRawText(),
        _ => throw new ErroDeDocumento($"'{chave}' deve ser texto. Exemplo: {Exemplo}.")
    };

    private static (string Texto, bool Desc) Sentido(string sort)
    {
        string s = sort.Trim();
        return s.StartsWith('-') ? (s[1..].Trim(), true) : (s.TrimStart('+').Trim(), false);
    }

    private static string Descrever(Filtro f) =>
        f.Op == "contains" ? $"{f.Col.Nome} contém \"{f.Valor}\"" : $"{f.Col.Nome} {f.Op} {f.Valor}";

    // ─────────────────────────────────────────────────────────────────────────
    // Colunas
    // ─────────────────────────────────────────────────────────────────────────

    private static string Nome(Grade grade, int cab, int c)
    {
        string t = Apresentacao.Achatar(grade[cab, c].Texto.Trim());
        return t.Length == 0 ? Grade.Letra(c) : Apresentacao.Curto(t, 40);
    }

    private static Coluna Achar(string nome, Grade grade, int cab, List<Coluna> colunas)
    {
        var (col, erro) = Tentar(nome, grade, cab, colunas);
        return col ?? throw new ErroDeDocumento(erro!);
    }

    /// <summary>
    /// Pelo nome do cabeçalho (exato, depois "contém" se só um servir) ou pela letra. O nome
    /// vem antes da letra: numa aba cujo cabeçalho é "A", "A" é essa coluna, não a primeira.
    /// </summary>
    private static (Coluna? Col, string? Erro) Tentar(string nome, Grade grade, int cab, List<Coluna> colunas)
    {
        string alvo = Apresentacao.Normalizar(nome.Trim());
        var cabecalhos = colunas.Select(c => (c, n: Apresentacao.Normalizar(grade[cab, c.Numero].Texto.Trim()))).ToList();

        var exatos = cabecalhos.Where(x => x.n.Length > 0 && x.n == alvo).ToList();
        if (exatos.Count >= 1) return (exatos[0].c, null);

        string letras = nome.Trim();
        if (letras.Length is > 0 and <= 3 && letras.All(char.IsAsciiLetter)
            && Grade.Coluna(letras) is int n and > 0 && n <= grade.Colunas)
            return (colunas[n - 1], null);

        var parecidos = alvo.Length == 0 ? new() : cabecalhos.Where(x => x.n.Contains(alvo, StringComparison.Ordinal)).ToList();
        if (parecidos.Count == 1) return (parecidos[0].c, null);

        string lista = string.Join(" | ", colunas.Select(c => $"{Grade.Letra(c.Numero)} {c.Nome}"));
        if (parecidos.Count > 1)
            return (null, $"a coluna \"{nome}\" serve para mais de uma: "
                          + $"{string.Join(", ", parecidos.Select(x => $"{Grade.Letra(x.c.Numero)} {x.c.Nome}"))}. "
                          + "Use o nome inteiro do cabeçalho ou a letra.");
        return (null, $"não achei a coluna \"{nome}\". Colunas (cabeçalho na linha {cab}): {lista}. "
                      + "Use o nome do cabeçalho ou a letra da coluna.");
    }

    private static string Compacto(string s) => Apresentacao.Normalizar(s).Replace(" ", "");

    private static string Plural(int n, string um, string varios) => n == 1 ? $"1 {um}" : $"{n.ToString(Br)} {varios}";
}
