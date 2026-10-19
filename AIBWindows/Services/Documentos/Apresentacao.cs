using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIB.Services.Documentos;

/// <summary>
/// Como a planilha e o documento chegam ao modelo: <c>info</c>, <c>read</c> e <c>find</c>.
/// <para>
/// Toda linha leva o endereço — célula (<c>B12</c>), parágrafo (<c>¶12</c>) ou página — para o
/// modelo poder citar e o usuário conferir. A <c>ler-planilha</c> devolvia texto separado por
/// tab, sem número de linha nem letra de coluna.
/// </para>
/// </summary>
public static class Apresentacao
{
    /// <summary>Quantos achados o <c>find</c> mostra.</summary>
    public const int Achados = 50;

    private const int TetoDaCelula = 300;

    // ─────────────────────────────────────────────────────────────────────────
    // Planilha
    // ─────────────────────────────────────────────────────────────────────────

    public static string Info(Pasta pasta, string caminho)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Planilha '{Path.GetFileName(caminho)}' ({pasta.Formato.ToString().ToLowerInvariant()}), "
                      + (pasta.Abas.Count == 1 ? "1 aba:" : $"{pasta.Abas.Count} abas:"));

        foreach (var aba in pasta.Abas)
        {
            string oculta = aba.Oculta ? " (oculta)" : "";
            if (aba.Colunas == 0)
            {
                sb.AppendLine($"- \"{aba.Nome}\"{oculta}: vazia");
                continue;
            }

            sb.AppendLine($"- \"{aba.Nome}\"{oculta}: A{aba.PrimeiraLinha}:{Grade.Letra(aba.Colunas)}{aba.Linhas} "
                          + $"({aba.Linhas - aba.PrimeiraLinha + 1} linhas, {aba.Colunas} colunas)");

            int? cab = Cabecalho(aba);
            if (cab is int l)
            {
                var nomes = Enumerable.Range(1, Math.Min(aba.Colunas, 30))
                    .Select(c => (c, t: aba[l, c].Texto))
                    .Where(x => x.t.Length > 0)
                    .Select(x => $"{Grade.Letra(x.c)} {Curto(x.t, 40)}");
                sb.AppendLine($"  cabeçalho (linha {l}): {string.Join(" | ", nomes)}");
            }

            int formulas = 0;
            for (int r = aba.PrimeiraLinha; r <= aba.Linhas && formulas < 1000; r++)
                for (int c = 1; c <= aba.Colunas; c++)
                    if (aba[r, c].Formula != null) formulas++;
            if (formulas > 0) sb.AppendLine($"  {(formulas >= 1000 ? "1000+" : formulas.ToString())} células com fórmula");
        }

        sb.Append("Leia um trecho com action='read' (sheet, range 'A1:F200' ou '10:60'). Contas: action='query'.");
        return sb.ToString();
    }

    /// <summary>A primeira linha não vazia, nas cinco primeiras: é quase sempre o cabeçalho.</summary>
    public static int? Cabecalho(Grade aba)
    {
        for (int r = aba.PrimeiraLinha; r <= Math.Min(aba.Linhas, aba.PrimeiraLinha + 4); r++)
            if (!aba.LinhaVazia(r)) return r;
        return null;
    }

    public static string Ler(Pasta pasta, string? nomeDaAba, string? intervalo, int teto)
    {
        var aba = AbaOuErro(pasta, nomeDaAba);
        if (aba.Colunas == 0) return $"A aba \"{aba.Nome}\" está vazia.";

        var (l1, c1, l2, c2) = Intervalo(intervalo, aba);
        int linhasMax = Math.Max(20, LimitesDoProvedor.Atual.LinhasDeLeitura);

        var sb = new StringBuilder();
        var corpo = new StringBuilder();
        corpo.Append('#');
        for (int c = c1; c <= c2; c++) corpo.Append('\t').Append(Grade.Letra(c));
        corpo.AppendLine();

        int ultima = l1 - 1, mostradas = 0;
        for (int r = l1; r <= l2; r++)
        {
            if (aba.LinhaVazia(r)) { ultima = r; continue; }

            var linha = new StringBuilder();
            linha.Append(r);
            for (int c = c1; c <= c2; c++) linha.Append('\t').Append(Mostrar(aba[r, c]));
            string texto = linha.ToString().TrimEnd('\t');

            if (mostradas >= linhasMax || corpo.Length + texto.Length > teto) break;
            corpo.AppendLine(texto);
            mostradas++;
            ultima = r;
        }

        sb.AppendLine($"Aba \"{aba.Nome}\" · linhas {l1}–{ultima} de {aba.Linhas} · colunas {Grade.Letra(c1)}–{Grade.Letra(c2)}"
                      + " · ⟨=…⟩ é a fórmula da célula");
        sb.Append(corpo);

        if (ultima < l2)
            sb.Append($"[parou na linha {ultima}; continue com range='{ultima + 1}:{l2}', ou use 'query' para contar e filtrar]");

        return sb.ToString().TrimEnd();
    }

    public static string Buscar(Pasta pasta, string texto, int teto)
    {
        string alvo = Normalizar(texto);
        var achados = new List<string>();
        int total = 0;

        foreach (var aba in pasta.Abas)
        {
            for (int r = aba.PrimeiraLinha; r <= aba.Linhas; r++)
            {
                for (int c = 1; c <= aba.Colunas; c++)
                {
                    var cel = aba[r, c];
                    if (!Normalizar(cel.Texto).Contains(alvo) && !(cel.Formula != null && Normalizar(cel.Formula).Contains(alvo)))
                        continue;

                    total++;
                    if (achados.Count >= Achados) continue;

                    var linha = Enumerable.Range(1, aba.Colunas).Select(x => aba[r, x].Texto).Where(t => t.Length > 0);
                    achados.Add($"{Ref(aba, r, c)}: {Curto(cel.Texto, 120)}   (linha {r}: {Curto(string.Join(" | ", linha), 200)})");
                }
            }
        }

        if (total == 0) return $"Nenhuma célula contém '{texto}' (sem diferenciar maiúsculas nem acentos).";

        var sb = new StringBuilder($"{total} célula(s) com '{texto}'");
        sb.AppendLine(total > achados.Count ? $"; mostrando {achados.Count}:" : ":");
        foreach (var a in achados)
        {
            if (sb.Length + a.Length > teto) break;
            sb.AppendLine(a);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>"Ramais!B12" — com aspas quando o nome da aba tem espaço.</summary>
    public static string Ref(Grade aba, int linha, int coluna)
    {
        string nome = aba.Nome.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_') ? $"'{aba.Nome}'" : aba.Nome;
        return $"{nome}!{Grade.Letra(coluna)}{linha}";
    }

    public static Grade AbaOuErro(Pasta pasta, string? nome) =>
        pasta.Aba(nome) ?? throw new ErroDeDocumento(
            $"não há aba \"{nome}\". Abas: {string.Join(", ", pasta.Abas.Select(a => $"\"{a.Nome}\""))}.");

    /// <summary>
    /// Lê "A1:F200", "10:60" (linhas), "B:D" (colunas), "B5" (uma célula) ou nada (a aba toda).
    /// </summary>
    public static (int L1, int C1, int L2, int C2) Intervalo(string? texto, Grade aba)
    {
        int ultimaL = Math.Max(aba.Linhas, aba.PrimeiraLinha), ultimaC = Math.Max(aba.Colunas, 1);
        if (string.IsNullOrWhiteSpace(texto)) return (aba.PrimeiraLinha, 1, ultimaL, ultimaC);

        string t = texto.Trim().Replace("$", "");
        int bang = t.LastIndexOf('!');
        if (bang >= 0) t = t[(bang + 1)..];

        var partes = t.Split(':');
        if (partes.Length > 2) throw new ErroDeDocumento($"intervalo '{texto}' ilegível. Use 'A1:F200', '10:60' ou 'B:D'.");

        var (la, ca) = Ponto(partes[0], texto);
        var (lb, cb) = partes.Length == 2 ? Ponto(partes[1], texto) : (la, ca);

        int l1 = la ?? aba.PrimeiraLinha, l2 = lb ?? ultimaL;
        int c1 = ca ?? 1, c2 = cb ?? ultimaC;
        if (l2 < l1) (l1, l2) = (l2, l1);
        if (c2 < c1) (c1, c2) = (c2, c1);
        return (Math.Max(1, l1), Math.Max(1, c1), Math.Min(l2, ultimaL), Math.Min(c2, Math.Max(ultimaC, c1)));
    }

    /// <summary>"B12" → (12, 2); "12" → (12, null); "B" → (null, 2).</summary>
    public static (int? Linha, int? Coluna) Ponto(string parte, string original)
    {
        string p = parte.Trim().ToUpperInvariant();
        int i = 0;
        while (i < p.Length && char.IsLetter(p[i])) i++;
        string letras = p[..i], numero = p[i..];

        int? coluna = letras.Length > 0 ? Grade.Coluna(letras) : null;
        int? linha = numero.Length > 0 && int.TryParse(numero, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

        if ((letras.Length > 0 && coluna is 0) || (numero.Length > 0 && linha is null or < 1) || (coluna is null && linha is null))
            throw new ErroDeDocumento($"intervalo '{original}' ilegível. Use 'A1:F200', '10:60' ou 'B:D'.");

        return (linha, coluna);
    }

    /// <summary>A célula numa linha de tabela: sem tab nem quebra, com a fórmula entre ⟨⟩.</summary>
    private static string Mostrar(Celula c)
    {
        string t = Curto(Achatar(c.Texto), TetoDaCelula);
        return c.Formula == null ? t : $"{t} ⟨={Curto(c.Formula, 120)}⟩";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Documento
    // ─────────────────────────────────────────────────────────────────────────

    public static string Info(Documento doc, string caminho)
    {
        var sb = new StringBuilder();
        string nome = Path.GetFileName(caminho);

        if (doc.Formato == Formato.Pdf)
        {
            int chars = doc.Blocos.Sum(b => b.Texto.Length);
            sb.AppendLine($"PDF '{nome}': {doc.Paginas} página(s), ~{chars} caracteres de texto.");
            if (chars == 0)
                sb.AppendLine("Nenhum texto extraível: provavelmente é imagem escaneada. Esta ferramenta não faz OCR.");
            else
                foreach (var b in doc.Blocos.Take(5))
                    sb.AppendLine($"  p.{b.Numero}: {Curto(Achatar(b.Texto), 120)}");
            sb.Append("Leia páginas com action='read' e range '2-4'; procure com 'find'.");
            return sb.ToString();
        }

        var titulos = doc.Blocos.Where(b => b.Tipo == TipoDeBloco.Titulo).ToList();
        int tabelas = doc.Blocos.Count(b => b.Tipo == TipoDeBloco.Tabela);
        sb.AppendLine($"Word '{nome}': {doc.Blocos.Count} blocos (¶), {titulos.Count} título(s), {tabelas} tabela(s).");
        if (titulos.Count > 0)
        {
            sb.AppendLine("Títulos:");
            foreach (var t in titulos.Take(40))
                sb.AppendLine($"  ¶{t.Numero} {new string(' ', Math.Max(0, t.Nivel - 1) * 2)}{Curto(t.Texto, 100)}");
            if (titulos.Count > 40) sb.AppendLine($"  + {titulos.Count - 40} títulos");
        }
        sb.Append("Leia com action='read' e range de ¶ ('12-40'); edite citando o ¶.");
        return sb.ToString();
    }

    public static string Ler(Documento doc, string? intervalo, int teto)
    {
        int primeiro = doc.Blocos.Count == 0 ? 0 : doc.Blocos[0].Numero;
        int ultimo = doc.Blocos.Count == 0 ? 0 : doc.Blocos[^1].Numero;
        if (doc.Blocos.Count == 0)
            return doc.Formato == Formato.Pdf
                ? "O PDF não tem texto extraível (imagem escaneada?). Esta ferramenta não faz OCR."
                : "O documento está vazio.";

        var (de, ate) = Faixa(intervalo, primeiro, ultimo);
        var sb = new StringBuilder();
        int parou = de - 1;

        foreach (var b in doc.Blocos.Where(b => b.Numero >= de && b.Numero <= ate))
        {
            string texto = Bloco(b, doc.Formato);
            if (sb.Length + texto.Length > teto && sb.Length > 0) break;
            sb.AppendLine(texto);
            parou = b.Numero;
        }

        string unidade = doc.Formato == Formato.Pdf ? "páginas" : "¶";
        string cabeca = $"{(doc.Formato == Formato.Pdf ? "PDF" : "Word")} · {unidade} {de}–{parou} de {ultimo}\n";
        if (parou < ate) sb.Append($"[parou em {parou}; continue com range='{parou + 1}-{ate}']");
        return (cabeca + sb).TrimEnd();
    }

    public static string Buscar(Documento doc, string texto, int teto)
    {
        string alvo = Normalizar(texto);
        var achados = new List<string>();
        int total = 0;

        foreach (var b in doc.Blocos)
        {
            string conteudo = b.Tabela == null ? b.Texto : string.Join(" | ", b.Tabela.SelectMany(l => l));
            string norm = Normalizar(conteudo);
            int pos = norm.IndexOf(alvo, StringComparison.Ordinal);
            if (pos < 0) continue;

            total++;
            if (achados.Count >= Achados) continue;

            // Normalizar não muda o comprimento (só tira acento e baixa a caixa), então a
            // posição vale no texto original.
            int ini = Math.Max(0, pos - 80), fim = Math.Min(conteudo.Length, pos + alvo.Length + 80);
            string trecho = (ini > 0 ? "…" : "") + Achatar(conteudo[ini..fim]) + (fim < conteudo.Length ? "…" : "");
            string onde = doc.Formato == Formato.Pdf ? $"p.{b.Numero}" : $"¶{b.Numero}";
            string secao = b.Secao != null && doc.Formato != Formato.Pdf ? $" ({Curto(b.Secao, 50)})" : "";
            achados.Add($"{onde}{secao}: {trecho}");
        }

        if (total == 0) return $"Nada contém '{texto}' (sem diferenciar maiúsculas nem acentos).";

        var sb = new StringBuilder($"{total} trecho(s) com '{texto}'");
        sb.AppendLine(total > achados.Count ? $"; mostrando {achados.Count}:" : ":");
        foreach (var a in achados)
        {
            if (sb.Length + a.Length > teto) break;
            sb.AppendLine(a);
        }
        return sb.ToString().TrimEnd();
    }

    private static string Bloco(Bloco b, Formato formato)
    {
        if (formato == Formato.Pdf) return $"--- página {b.Numero} ---\n{b.Texto.TrimEnd()}";

        return b.Tipo switch
        {
            TipoDeBloco.Titulo => $"¶{b.Numero} [Título {b.Nivel}] {Achatar(b.Texto)}",
            TipoDeBloco.Item => $"¶{b.Numero} • {Achatar(b.Texto)}",
            TipoDeBloco.Tabela when b.Tabela != null =>
                $"¶{b.Numero} [Tabela {b.Tabela.Count}×{(b.Tabela.Count == 0 ? 0 : b.Tabela.Max(l => l.Count))}]\n"
                + string.Join("\n", b.Tabela.Select((l, i) => $"  {i + 1}: {string.Join(" | ", l.Select(c => Curto(Achatar(c), 120)))}")),
            _ => $"¶{b.Numero} {Achatar(b.Texto)}"
        };
    }

    /// <summary>"12-40", "12", "12-" ou nada. Fora da faixa, encosta nas pontas.</summary>
    public static (int De, int Ate) Faixa(string? texto, int primeiro, int ultimo)
    {
        if (string.IsNullOrWhiteSpace(texto)) return (primeiro, ultimo);

        string t = texto.Trim().TrimStart('¶', 'p', 'P', '.').Replace("¶", "");
        var partes = t.Split(new[] { '-', ':', '–' }, StringSplitOptions.TrimEntries);
        if (partes.Length > 2 || !int.TryParse(partes[0], out int de))
            throw new ErroDeDocumento($"faixa '{texto}' ilegível. Use '12-40' ou '3'.");

        int ate = partes.Length == 1 ? de
            : partes[1].Length == 0 ? ultimo
            : int.TryParse(partes[1], out int a) ? a
            : throw new ErroDeDocumento($"faixa '{texto}' ilegível. Use '12-40' ou '3'.");

        if (ate < de) (de, ate) = (ate, de);
        return (Math.Max(primeiro, de), Math.Min(ultimo, ate));
    }

    // ─────────────────────────────────────────────────────────────────────────

    public static string Curto(string texto, int max) =>
        texto.Length <= max ? texto : texto[..(max - 1)] + "…";

    public static string Achatar(string texto) =>
        texto.Replace("\r\n", " ⏎ ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    /// <summary>Minúsculas e sem acento, com o MESMO comprimento do original.</summary>
    public static string Normalizar(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (char ch in texto)
        {
            string d = ch.ToString().Normalize(NormalizationForm.FormD);
            char basico = d.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark ? d[0] : ch;
            sb.Append(char.ToLowerInvariant(basico));
        }
        return sb.ToString();
    }
}
