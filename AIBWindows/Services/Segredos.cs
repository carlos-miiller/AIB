using System;
using System.Text.RegularExpressions;

namespace AIB.Services;

/// <summary>
/// Tira do resultado de uma ferramenta o que tem cara de segredo, ANTES de o texto ir ao modelo.
/// <para>
/// Caso real: um <c>docker inspect</c> pedido numa conversa devolveu as variáveis de ambiente
/// do contêiner, com a senha do banco (<c>ConnectionStrings__DefaultConnection</c>) e o
/// <c>Auth__JwtSecret</c>. Os dois foram ao provedor do modelo e ficaram no <c>raw.jsonl</c>, em
/// texto claro. Ninguém pediu segredo nenhum: ele veio de carona na saída de um comando.
/// </para>
/// <para>
/// Como o <c>raw.jsonl</c>, o histórico e a tela gravam o que o modelo recebeu, filtrar na
/// saída do <see cref="ToolRegistry"/> limpa todos de uma vez.
/// </para>
/// <para>
/// É por PADRÃO DE TEXTO, e portanto best-effort: pega nome suspeito com valor ao lado e os
/// formatos conhecidos de chave. Senha solta, sem nome por perto, passa. O nome fica e só o
/// valor sai, para o modelo ainda entender o que leu.
/// </para>
/// </summary>
public static class Segredos
{
    /// <summary>O que fica no lugar do valor.</summary>
    public const string Omitido = "[SEGREDO OMITIDO]";

    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(2);
    private const RegexOptions Opcoes = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Como TERMINA um nome que guarda segredo. Só o fim conta: <c>max_tokens</c> e
    /// <c>TokenCount</c> não são segredo, <c>GITHUB_TOKEN</c> e <c>JwtSecret</c> são.
    /// </summary>
    private const string Fim =
        @"(?:password|passwd|pwd|(?<![a-z])pass|senha|secret|token|api[_-]?key|access[_-]?key|secret[_-]?key|private[_-]?key|credentials?)";

    /// <summary>O nome inteiro: termina como segredo, ou é uma string de conexão.</summary>
    private const string Nome = @"(?<![\w.])(?:[\w.:\-]*?" + Fim + @"|connectionstrings?[\w.:\-]*)";

    /// <summary>
    /// <c>NOME=valor</c> colado, como em variável de ambiente, .env, string de conexão e URL.
    /// Com espaço em volta do <c>=</c> é atribuição de código, e fica para <see cref="EntreAspas"/>.
    /// </summary>
    private static readonly Regex Colado = new(
        "(?<nome>" + Nome + @")=(?<valor>[^\s;""'&,)<>]{4,})(?<depois>.?)", Opcoes, Prazo);

    /// <summary><c>"senha": "valor"</c>, <c>senha = "valor"</c>, <c>senha: 'valor'</c>.</summary>
    private static readonly Regex EntreAspas = new(
        "(?<antes>" + Nome + @"[""']?\s*[:=]\s*(?<aspa>[""']))(?<valor>[^""'\r\n]{4,})(?=\k<aspa>)", Opcoes, Prazo);

    /// <summary><c>senha: valor</c> sem aspas (YAML, saída de comando).</summary>
    private static readonly Regex DoisPontos = new(
        "(?<antes>" + Nome + @":[ \t]+)(?<valor>[^\s""']{6,})", Opcoes, Prazo);

    /// <summary>Formatos que são segredo onde quer que apareçam.</summary>
    private static readonly Regex[] Conhecidos =
    {
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", Opcoes, Prazo),
        new(@"\beyJ[\w\-]{10,}\.eyJ[\w\-]{10,}\.[\w\-]{10,}", RegexOptions.Compiled, Prazo),
        new(@"\bsk-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled, Prazo),
        new(@"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_\w{30,})", RegexOptions.Compiled, Prazo),
        new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled, Prazo),
        new(@"\bxox[baprs]-[\w\-]{10,}", RegexOptions.Compiled, Prazo),
    };

    /// <summary><c>Authorization: Bearer xyz</c> e <c>Basic xyz</c>.</summary>
    private static readonly Regex Cabecalho = new(
        @"(?<antes>\b(?:Bearer|Basic)\s+)(?<valor>[\w\-.=+/]{16,})", Opcoes, Prazo);

    /// <summary>A senha de <c>esquema://usuario:senha@host</c>.</summary>
    private static readonly Regex NaUrl = new(
        @"(?<antes>://[^/\s:@]+:)(?<valor>[^@\s/]{3,})(?=@)", Opcoes, Prazo);

    /// <summary><c>objeto.campo</c>: expressão de código, não valor literal.</summary>
    private static readonly Regex Membro = new(@"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+$", RegexOptions.Compiled, Prazo);

    /// <summary>O texto sem os valores com cara de segredo. Texto sem nenhum volta intacto.</summary>
    public static string Redigir(string? texto) => Redigir(texto, out _);

    /// <param name="quantos">Quantos valores foram trocados.</param>
    public static string Redigir(string? texto, out int quantos)
    {
        quantos = 0;
        if (string.IsNullOrEmpty(texto)) return texto ?? "";

        int n = 0;

        try
        {
            string limpo = texto;

            foreach (var formato in Conhecidos)
                limpo = formato.Replace(limpo, _ => { n++; return Omitido; });

            limpo = Cabecalho.Replace(limpo, m => { n++; return m.Groups["antes"].Value + Omitido; });
            limpo = NaUrl.Replace(limpo, m => { n++; return m.Groups["antes"].Value + Omitido; });
            limpo = EntreAspas.Replace(limpo, m => Trocar(m, ref n));
            limpo = DoisPontos.Replace(limpo, m => TemCaraDeValor(m.Groups["valor"].Value) ? Trocar(m, ref n) : m.Value);

            limpo = Colado.Replace(limpo, m =>
            {
                string valor = m.Groups["valor"].Value;
                string depois = m.Groups["depois"].Value;

                // Código, e não configuração: `password=self.password)`, `token=ct,`,
                // `senha=Ler(...)`. Redigir isso estragaria a leitura de código-fonte sem
                // esconder segredo nenhum.
                if (depois is ")" or "," || valor.IndexOfAny(new[] { '(', '[', '{' }) >= 0 || Membro.IsMatch(valor))
                    return m.Value;

                n++;
                return m.Groups["nome"].Value + "=" + Omitido + depois;
            });

            quantos = n;
            return limpo;
        }
        catch (RegexMatchTimeoutException)
        {
            // Texto patológico para as expressões. Devolver o original seria vazar; a resposta
            // some inteira, e o modelo é avisado.
            quantos = 1;
            return "[saída omitida: não foi possível conferir se há segredos nela]";
        }
    }

    /// <summary>
    /// O aviso que acompanha um resultado com valores omitidos. Sem ele o modelo vê a marca,
    /// conclui que o comando falhou e tenta de novo por outro caminho.
    /// </summary>
    public static string Aviso(int quantos) =>
        $"\n\n({quantos} valor(es) com cara de segredo omitido(s) pelo AIB antes de chegar a você. "
        + "Não tente obtê-los por outro caminho; se precisar de um, peça ao usuário.)";

    /// <summary>
    /// A recusa de <c>write</c> e <c>edit</c> a um texto com a marca. O modelo lê um .env com a
    /// senha omitida e devolve o arquivo inteiro: sem isto, a marca seria gravada por cima da
    /// senha de verdade.
    /// </summary>
    public const string RecadoDeGravacao =
        "ERRO: o texto traz a marca '" + Omitido + "', que o AIB pôs no lugar de um valor que você não "
        + "recebeu. Gravar isso apagaria o valor verdadeiro. Altere só as linhas sem a marca, ou peça ao "
        + "usuário para mexer nessa linha.";

    /// <summary>Se o texto traz a marca de omissão: não pode ser gravado como se fosse o valor.</summary>
    public static bool TemMarca(string? texto) =>
        texto != null && texto.Contains(Omitido, StringComparison.Ordinal);

    private static string Trocar(Match m, ref int n)
    {
        if (m.Groups["valor"].Value == Omitido) return m.Value;

        n++;
        return m.Groups["antes"].Value + Omitido;
    }

    /// <summary>
    /// Sem aspas e depois de dois-pontos, quase tudo é código (<c>senha: string;</c>). Só passa
    /// por valor o que mistura letra com número, ou traz símbolo de senha.
    /// </summary>
    private static bool TemCaraDeValor(string valor)
    {
        bool letra = false, numero = false;

        foreach (char c in valor)
        {
            if (char.IsLetter(c)) letra = true;
            else if (char.IsDigit(c)) numero = true;
            else if ("!@#$%^&*+/=".IndexOf(c) >= 0) return true;
        }

        return letra && numero;
    }
}
