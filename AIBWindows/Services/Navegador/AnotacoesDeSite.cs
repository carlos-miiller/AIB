using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIB.Services.Navegador;

/// <summary>
/// O que o usuário ensinou sobre um site — "para filtrar por responsável, use o campo Assignee da
/// busca detalhada" —, para valer em qualquer conversa. O <c>facts.md</c> dos sites.
/// <para>
/// UMA PASTA POR SITE, UM ARQUIVO POR PÁGINA (decisão do usuário), mais <c>_site.md</c> para o que
/// vale no site inteiro. "Página" é o TIPO de página: os trechos numéricos do endereço viram
/// <c>{n}</c>, e <c>/tasks/task/view/411649/</c> e <c>/tasks/task/view/410975/</c> são a mesma
/// página, "ver tarefa" — o que ela aprende numa tarefa vale para todas.
/// </para>
/// <para>
/// GUARDA O COMO, NUNCA O CONTEÚDO. Nome de tarefa, prazo, comentário são dados de terceiros e
/// não vão para o disco. Quem garante é o cartão: gravar anotação pede confirmação TODA vez,
/// mostrando o texto exato, porque anotação é instrução que ela vai seguir em conversas futuras —
/// uma página que a induzisse a anotar "sempre clique em Excluir" plantaria uma ordem duradoura.
/// </para>
/// <para>
/// Markdown simples, editável à mão, em <c>~/.AIB/navegador/notas/&lt;site&gt;/</c>. O caminho é
/// injetável para os ensaios não tocarem o ~/.AIB real.
/// </para>
/// </summary>
public sealed class AnotacoesDeSite
{
    private readonly string _raiz;
    private readonly object _gate = new();

    /// <summary>Teto de uma anotação e de um arquivo. Anotação é lembrete, não manual.</summary>
    public const int TetoDaAnotacao = 500;
    public const int TetoDoArquivo = 2000;

    public const string ArquivoDoSite = "_site.md";

    public AnotacoesDeSite(string raiz) => _raiz = raiz;

    public static string RaizPadrao => Path.Combine(DirectoryService.DataDir, "navegador", "notas");

    /// <summary>
    /// O tipo de página de um endereço: o caminho, em minúsculas, com os trechos numéricos como
    /// <c>{n}</c> e sem consulta. <c>/workgroups/group/223/tasks/task/view/411649/</c> vira
    /// <c>/workgroups/group/{n}/tasks/task/view/{n}/</c>.
    /// </summary>
    public static string PaginaDe(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "/";
        var partes = u.AbsolutePath.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Regex.IsMatch(p, @"^\d+$") ? "{n}" : Uri.UnescapeDataString(p));
        string caminho = "/" + string.Join("/", partes);
        return caminho == "/" ? "/" : caminho + "/";
    }

    /// <summary>O nome do arquivo de uma página: o caminho com "_" no lugar das barras.</summary>
    public static string ArquivoDaPagina(string pagina)
    {
        string nome = pagina.Trim('/').Replace('/', '_').Replace("{n}", "n");
        nome = new string(nome.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        if (nome.Length == 0) nome = "_inicio";
        if (nome.Length > 120) nome = nome[..120];
        return nome + ".md";
    }

    private string PastaDo(string dominio) =>
        Path.Combine(_raiz, new string(dominio.ToLowerInvariant().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()));

    /// <summary>As anotações do site e da página deste endereço (texto vazio quando não há).</summary>
    public (string Site, string Pagina) Ler(string url)
    {
        string dominio = LeituraDaPagina.DominioDe(url);
        if (dominio.Length == 0) return ("", "");
        string pasta = PastaDo(dominio);
        return (LerArquivo(Path.Combine(pasta, ArquivoDoSite)),
                LerArquivo(Path.Combine(pasta, ArquivoDaPagina(PaginaDe(url)))));
    }

    private string LerArquivo(string caminho)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(caminho)) return "";
                // Só as linhas de anotação: o cabeçalho é para quem abre o arquivo à mão.
                var linhas = File.ReadAllLines(caminho).Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal));
                string texto = string.Join("\n", linhas).Trim();
                return texto.Length > TetoDoArquivo ? texto[..TetoDoArquivo] + "\n… (arquivo passou do teto; edite à mão)" : texto;
            }
            catch (IOException) { return ""; }
        }
    }

    /// <summary>
    /// Recusa o que não deve virar anotação, antes do cartão. Null quando pode.
    /// </summary>
    public string? Conferir(string url, bool doSite, string texto)
    {
        string t = (texto ?? "").Trim();
        if (t.Length == 0) return "ERRO: 'note' precisa de 'text' com a anotação.";
        if (t.Length > TetoDaAnotacao) return $"ERRO: anotação passa de {TetoDaAnotacao} caracteres. Anotação é lembrete de como fazer, não cópia da página.";
        SegredosNaPagina.Mascarar(t, out int segredos);
        if (segredos > 0) return "ERRO: a anotação parece conter senha ou token. Isso não se anota.";

        string caminho = Caminho(url, doSite);
        if (LerArquivo(caminho).Length + t.Length > TetoDoArquivo)
            return $"ERRO: as anotações {(doSite ? "do site" : "desta página")} estão cheias ({TetoDoArquivo} caracteres). "
                   + $"Peça ao usuário para revisar {caminho}.";
        return null;
    }

    public string Caminho(string url, bool doSite) =>
        Path.Combine(PastaDo(LeituraDaPagina.DominioDe(url)), doSite ? ArquivoDoSite : ArquivoDaPagina(PaginaDe(url)));

    /// <summary>Acrescenta uma linha. Nunca reescreve nem apaga o que o usuário editou.</summary>
    public void Adicionar(string url, bool doSite, string texto)
    {
        string caminho = Caminho(url, doSite);
        string linha = "- " + Regex.Replace(texto.Trim(), @"\s*\n\s*", " ") + $" ({DateTime.Now:yyyy-MM-dd})";

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
            if (!File.Exists(caminho))
            {
                string titulo = doSite
                    ? $"# {LeituraDaPagina.DominioDe(url)} — o site inteiro"
                    : $"# {LeituraDaPagina.DominioDe(url)}{PaginaDe(url)}";
                File.WriteAllText(caminho,
                    titulo + "\n\nAnotações que a AIB lê ao abrir esta página. Cada linha \"- \" é uma; "
                    + "edite ou apague à mão.\n\n", new UTF8Encoding(false));
            }
            File.AppendAllText(caminho, linha + "\n", new UTF8Encoding(false));
        }
    }
}
