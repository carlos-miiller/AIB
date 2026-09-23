using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIB.Services.Navegador;

/// <summary>
/// Um elemento da página como o <c>Snapshot.js</c> o viu. <see cref="Ref"/> vazia é texto sem ação.
/// <para>
/// <see cref="NaTela"/> é "dentro da janela E por cima de tudo": o Bitrix empilha painéis (chat,
/// perfil, tarefa) e o que está coberto não é o que a pessoa vê.
/// </para>
/// </summary>
public sealed record NoDaPagina(
    string Ref, string Papel, string Texto, int Prof, bool Visivel, bool NaTela, int Quadro,
    string Href = "", bool Envia = false);

/// <summary>
/// Uma leitura da página, guardada em memória para a IA pesquisar em vez de receber tudo.
/// <para>
/// POR QUE NÃO A PÁGINA INTEIRA. No projeto de tarefas do Bitrix a árvore inteira tinha ≈7 mil
/// tokens; a vista (só o que está na tela e por cima), ≈900; a tabela filtrada, ≈900. O OCR da
/// tela foi medido e descartado: pouco texto, sem estrutura e sem como clicar
/// (prototipos/ProtoNavegador).
/// </para>
/// <para>
/// A VERSÃO VAI NA REF (<c>s3e40</c>). Depois de navegar, a ref antiga não vale mais: o elemento
/// pode nem existir, e clicar nela seria clicar no escuro.
/// </para>
/// <para>
/// NADA DAQUI VAI PARA O DISCO. É conteúdo de terceiros, e sai para o modelo embrulhado por
/// <see cref="Mail.ConteudoDeTerceiros.EmbrulharPagina"/>; segredo visível na tela já chega
/// mascarado (<see cref="SegredosNaPagina"/>).
/// </para>
/// </summary>
public sealed class LeituraDaPagina
{
    public int Versao { get; }
    public string Url { get; }
    public string Titulo { get; }
    public IReadOnlyList<NoDaPagina> Nos { get; }
    public int SegredosMascarados { get; }

    public LeituraDaPagina(int versao, string url, string titulo, IEnumerable<NoDaPagina> nos)
    {
        Versao = versao;
        Url = url;
        Titulo = titulo;

        // Mascara na ENTRADA: nenhuma saída (vista, busca, tabela, cartão) vê o original.
        int segredos = 0;
        Nos = nos.Select(n =>
        {
            string t = SegredosNaPagina.Mascarar(n.Texto, out int k);
            segredos += k;
            return k == 0 ? n : n with { Texto = t };
        }).ToList();
        SegredosMascarados = segredos;
    }

    /// <summary>O domínio da página (<c>cpaps.bitrix24.com</c>), ou vazio.</summary>
    public string Dominio => DominioDe(Url);

    public static string DominioDe(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";

    // ─────────────────────────────────────────────────────────────── saídas

    /// <summary>Teto de cada saída. Uma página com mil linhas não pode virar mil linhas no contexto.</summary>
    public const int TetoDaVista = 8000;
    public const int TetoDeAchados = 20;
    public const int TetoDeLinhasDaTabela = 80;

    private string Cabecalho => $"página s{Versao} · {Titulo}\n{Url}";

    /// <summary>
    /// O que está na tela agora, como uma pessoa vê. Termina com o que existe fora dela, para a
    /// IA saber que pode rolar, procurar ou pedir a tabela.
    /// </summary>
    public string Vista()
    {
        // Tabela cortada pela tela ganha o aviso colado na última linha visível. Visto no Bitrix:
        // depois de filtrar, a vista mostrou 4 linhas, e o modelo respondeu "a busca retornou 4
        // tarefas" — o aviso no rodapé ("tabelas: 1 (25 linhas)") ficava longe demais para ser lido.
        var avisos = new Dictionary<int, string>();
        foreach (var t in Tabelas())
        {
            var indices = IndicesDasLinhas(t.Indice).ToList();
            int naTela = indices.Count(i => Nos[i].NaTela);
            if (naTela > 0 && naTela < indices.Count)
                avisos[indices.Last(i => Nos[i].NaTela)] =
                    $"  … tabela {t.Ordem}: só {naTela} de {indices.Count} linhas estão na tela; "
                    + $"as outras existem fora dela — table {t.Ordem} traz todas";
        }

        var sb = new StringBuilder(Cabecalho).Append('\n');
        int escritos = 0;
        for (int i = 0; i < Nos.Count; i++)
        {
            var n = Nos[i];
            if (!n.Visivel || !n.NaTela) continue;
            string linha = Linha(n);
            if (sb.Length + linha.Length > TetoDaVista)
            {
                sb.AppendLine("… (vista cortada; use find ou table)");
                break;
            }
            sb.AppendLine(linha);
            if (avisos.TryGetValue(i, out var aviso)) sb.AppendLine(aviso);
            escritos++;
        }
        if (escritos == 0) sb.AppendLine("(nada visível na tela — a página pode estar carregando; tente view de novo)");

        int fora = Nos.Count(n => n.Visivel && !n.NaTela && n.Texto.Length > 0);
        var tabelas = Tabelas().Where(t => t.Linhas > 1).ToList();
        var rodape = new List<string>();
        if (fora > 0) rodape.Add($"{fora} elemento(s) fora da tela ou cobertos (scroll, find)");
        if (tabelas.Count > 0)
            rodape.Add("tabelas: " + string.Join(", ", tabelas.Select(t => $"{t.Ordem} ({t.Linhas} linhas)")) + " (table)");
        if (SegredosMascarados > 0) rodape.Add($"{SegredosMascarados} possível(is) segredo(s) mascarado(s)");
        if (rodape.Count > 0) sb.Append("— ").AppendLine(string.Join(" · ", rodape));

        return sb.ToString().TrimEnd();
    }

    /// <summary>Procura na página inteira (sem acento, sem caixa), com uma linha de contexto de cada lado.</summary>
    public string Achar(string termo)
    {
        var nos = Nos.Where(n => n.Visivel).ToList();
        string alvo = SemAcento(termo.Trim());
        if (alvo.Length == 0) return "ERRO: 'text' vazio. Diga o que procurar.";

        var achados = new List<int>();
        for (int i = 0; i < nos.Count; i++)
            if (SemAcento(nos[i].Texto).Contains(alvo, StringComparison.Ordinal)) achados.Add(i);

        if (achados.Count == 0) return $"{Cabecalho}\nnada com \"{termo}\" na página.";

        var sb = new StringBuilder(Cabecalho).Append('\n');
        int ultimo = -2;
        foreach (int i in achados.Take(TetoDeAchados))
        {
            for (int j = Math.Max(0, i - 1); j <= Math.Min(nos.Count - 1, i + 1); j++)
            {
                if (j <= ultimo) continue;
                if (j > ultimo + 1 && ultimo >= 0) sb.AppendLine("  ⋯");
                sb.Append(j == i ? "» " : "  ").Append(Linha(nos[j]).TrimStart());
                if (!nos[j].NaTela) sb.Append("  (fora da vista)");
                sb.Append('\n');
                ultimo = j;
            }
        }
        if (achados.Count > TetoDeAchados) sb.AppendLine($"… mais {achados.Count - TetoDeAchados} ocorrência(s); refine o termo.");
        return sb.ToString().TrimEnd();
    }

    public sealed record InfoDeTabela(int Ordem, int Indice, string Ref, int Linhas, bool NaTela, string Primeira);

    /// <summary>As tabelas da página, na ordem em que aparecem.</summary>
    public List<InfoDeTabela> Tabelas()
    {
        var lista = new List<InfoDeTabela>();
        for (int i = 0; i < Nos.Count; i++)
        {
            if (Nos[i].Papel != "tabela" || !Nos[i].Visivel) continue;
            var linhas = LinhasDe(i).ToList();
            lista.Add(new InfoDeTabela(lista.Count + 1, i, Nos[i].Ref, linhas.Count, Nos[i].NaTela,
                linhas.FirstOrDefault()?.Texto ?? ""));
        }
        return lista;
    }

    private IEnumerable<NoDaPagina> LinhasDe(int indice) => IndicesDasLinhas(indice).Select(j => Nos[j]);

    private IEnumerable<int> IndicesDasLinhas(int indice)
    {
        var t = Nos[indice];
        for (int j = indice + 1; j < Nos.Count && Nos[j].Prof > t.Prof && Nos[j].Quadro == t.Quadro; j++)
            if (Nos[j].Papel == "linha" && Nos[j].Visivel) yield return j;
    }

    // "Mostrar mais", "próxima página": a lista na página pode ser só o primeiro pedaço.
    private static readonly Regex MaisItens = new(
        @"^(próxima|proxima|next|seguinte|mostrar mais|show more|carregar mais|load more|ver mais|mais resultados|more)\b|^[›»>]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private NoDaPagina? ControleDeMaisItens() =>
        Nos.FirstOrDefault(n => n.Visivel && n.Papel is "link" or "botão" && MaisItens.IsMatch(n.Texto.Trim()));

    /// <summary>
    /// Uma tabela em texto compacto (uma linha por linha, células separadas por " | "), escolhida
    /// pela ordem (1, 2…) ou pela ref. Sem escolha, lista as tabelas — as de uma linha só são
    /// fichas ("Status: | Pending"), contadas à parte.
    /// </summary>
    public string Tabela(string? qual)
    {
        var tabelas = Tabelas();
        if (tabelas.Count == 0) return $"{Cabecalho}\nnenhuma tabela nesta página. Use view ou find.";

        InfoDeTabela? escolhida = null;
        string q = (qual ?? "").Trim();
        if (q.Length > 0)
        {
            escolhida = int.TryParse(q, out int ordem)
                ? tabelas.FirstOrDefault(t => t.Ordem == ordem)
                : tabelas.FirstOrDefault(t => t.Ref.Equals(q, StringComparison.OrdinalIgnoreCase));
        }

        if (escolhida == null)
        {
            var sb = new StringBuilder(Cabecalho).Append('\n');
            if (q.Length > 0) sb.AppendLine($"tabela '{q}' não existe nesta página.");
            int fichas = 0;
            foreach (var t in tabelas)
            {
                if (t.Linhas <= 1) { fichas++; continue; }
                sb.AppendLine($"{t.Ordem}. {t.Linhas} linhas{(t.NaTela ? "" : " (fora da vista)")}: {t.Primeira}");
            }
            if (fichas > 0) sb.AppendLine($"(+ {fichas} tabela(s) de uma linha só — fichas, não listas)");
            sb.Append("Peça uma com table e o número.");
            return sb.ToString();
        }

        var linhas = LinhasDe(escolhida.Indice).ToList();
        var saida = new StringBuilder(Cabecalho).Append('\n').Append($"tabela {escolhida.Ordem} ({linhas.Count} linhas)\n");
        foreach (var l in linhas.Take(TetoDeLinhasDaTabela)) saida.AppendLine(l.Texto);
        if (linhas.Count > TetoDeLinhasDaTabela)
            saida.AppendLine($"… mais {linhas.Count - TetoDeLinhasDaTabela} linha(s); filtre na própria página ou use find.");
        if (ControleDeMaisItens() is { } mais)
            saida.AppendLine($"(estas são as linhas carregadas; a página tem [{mais.Ref}] {mais.Papel} \"{mais.Texto}\" — pode haver mais itens)");
        return saida.ToString().TrimEnd();
    }

    // ─────────────────────────────────────────────────────────────── refs

    /// <summary>
    /// A ref completa, aceitando a forma curta (<c>e40</c>) como da leitura atual. Devolve null
    /// com o motivo quando é de outra leitura ou não existe.
    /// </summary>
    public (NoDaPagina? No, string? Recusa) Resolver(string? refe)
    {
        string r = (refe ?? "").Trim().ToLowerInvariant();
        if (Regex.IsMatch(r, @"^(?:f\d+)?e\d+$")) r = $"s{Versao}{r}";

        var m = Regex.Match(r, @"^s(\d+)(?:f\d+)?e\d+$");
        if (!m.Success) return (null, $"ERRO: ref '{refe}' inválida. Use a ref entre colchetes da vista (ex.: s{Versao}e40).");

        if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) != Versao)
            return (null, $"ERRO: a ref '{refe}' é de uma leitura antiga; a página mudou desde então (atual: s{Versao}). "
                          + "Use as refs da vista mais recente.");

        var no = Nos.FirstOrDefault(n => n.Ref == r);
        return no == null
            ? (null, $"ERRO: a ref '{refe}' não existe na leitura s{Versao}.")
            : (no, null);
    }

    // ─────────────────────────────────────────────────────────────── texto

    public static string Linha(NoDaPagina n)
    {
        string recuo = new(' ', Math.Min(n.Prof, 6) * 2);
        string r = n.Ref.Length > 0 ? $"[{n.Ref}] " : "";
        return n.Papel switch
        {
            "texto" => recuo + n.Texto,
            "linha" => recuo + r + n.Texto,
            _ => n.Texto.Length > 0 ? $"{recuo}{r}{n.Papel} \"{n.Texto}\"" : $"{recuo}{r}{n.Papel}",
        };
    }

    private static string SemAcento(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().ToLowerInvariant();
    }
}

/// <summary>
/// Senha colada em chat, token, chave: tudo o que está na tela iria para o provedor do modelo.
/// <para>
/// Visto no primeiro teste no Bitrix: a vista trouxe duas credenciais coladas num chat.
/// Heurística: palavra de 12+ caracteres que mistura maiúscula, minúscula e dois ou mais números
/// (com ou sem símbolo), ou 16 minúsculas quase sem vogal (o formato da senha de app do Google).
/// Nome de máquina e patrimônio (<c>CPAPS-NB0123</c>) é tudo maiúsculo e passa; endereço e
/// e-mail passam; citação grudada ("União.[3]") não conta.
/// </para>
/// </summary>
public static class SegredosNaPagina
{
    public const string Mascara = "[segredo mascarado]";

    private static readonly Regex Palavra = new(@"\S{10,}", RegexOptions.Compiled);
    private static readonly Regex Citacao = new(@"\[\w{0,4}\]", RegexOptions.Compiled);
    private static readonly Regex EnderecoDeEmail = new(@"^[\w.+-]+@[\w-]+\.[\w.]+$", RegexOptions.Compiled);
    private static readonly Regex SenhaDeApp = new("^[a-z]{16}$", RegexOptions.Compiled);

    public static string Mascarar(string texto, out int mascarados)
    {
        int n = 0;
        string r = Palavra.Replace(texto ?? "", m =>
        {
            string w = Citacao.Replace(m.Value, "").Trim('.', ',', ';', ':', '(', ')', '"', '\'');
            if (w.Contains("://", StringComparison.Ordinal) || w.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                || EnderecoDeEmail.IsMatch(w)) return m.Value;

            bool misturado = w.Length >= 12 && w.Count(char.IsDigit) >= 2
                             && w.Any(char.IsLower) && w.Any(char.IsUpper);
            bool senhaDeApp = SenhaDeApp.IsMatch(w) && w.Count(c => "aeiou".Contains(c)) <= 3;
            if (!misturado && !senhaDeApp) return m.Value;

            n++;
            return Mascara;
        });
        mascarados = n;
        return r;
    }
}
