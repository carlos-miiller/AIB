using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AIB.Services.Tools;

namespace AIB.Services;

/// <summary>
/// As pastas do próprio AIB — a de dados (<c>~/.AIB</c>) e a do programa (onde está o
/// <c>AIB.exe</c>) — são só leitura para as ferramentas: nada ali se grava, edita, move, renomeia
/// ou apaga, nem com o cartão.
/// <para>
/// Pedido do usuário (11/11/2026): "colocarmos uma trava em cima das ferramentas de ellen para ela
/// não conseguir mudar nada em sua pasta, tanto a de seu programa, quanto a na pasta de usuario".
/// Caso que motivou: a Ellen já reescreveu o script de uma skill que roda com o segredo do Bitrix,
/// e é o script que impede a skill de ler outras conversas do usuário. Com o cartão, a defesa
/// dependia de alguém ler cada mudança com atenção.
/// </para>
/// <para>
/// O que o AIB grava por conta própria (memória, backups do <c>doc</c>, anotações do navegador)
/// não passa por aqui: a trava olha só os caminhos que a IA manda. O <c>shell</c> também não:
/// não dá para saber de um comando qualquer o que ele escreve; ele segue no cartão.
/// </para>
/// </summary>
public static class PastasDoAib
{
    /// <summary>
    /// A recusa, se a chamada mexe em alguma das pastas; null se não mexe.
    /// <paramref name="dados"/> e <paramref name="programa"/> vazios são as de verdade — os ensaios
    /// passam temporárias.
    /// </summary>
    public static string? Recusa(string ferramenta, string argumentsJson, string? dados = null, string? programa = null)
    {
        string pastaDados = string.IsNullOrWhiteSpace(dados) ? DirectoryService.DataDir : dados!;
        string pastaPrograma = string.IsNullOrWhiteSpace(programa) ? AppContext.BaseDirectory : programa!;

        foreach (string alvo in Alvos(ferramenta, argumentsJson))
        {
            string real = Real(alvo);
            if (Sob(real, pastaDados)) return Mensagem(alvo, "na pasta de dados do AIB");
            if (Sob(real, pastaPrograma)) return Mensagem(alvo, "na pasta do programa AIB");
        }
        return null;
    }

    private static string Mensagem(string alvo, string onde) =>
        $"ACESSO NEGADO: '{alvo}' fica {onde}, que as ferramentas só podem ler. Nada foi feito. "
        + "Se o usuário quer mudar algo ali, diga que ele mesmo precisa fazer; não tente pelo shell.";

    /// <summary>Os caminhos que a chamada vai mudar. A origem de uma cópia só é lida.</summary>
    internal static IEnumerable<string> Alvos(string ferramenta, string argumentsJson)
    {
        // Um lote do 'file' se abre nas operações, cada uma no formato da ferramenta antiga.
        foreach (var (nome, args, _) in FileTool.Abrir(ferramenta, argumentsJson, ""))
        {
            switch (nome)
            {
                case Ferramentas.Gravar:
                case Ferramentas.Editar:
                    if (Texto(args, "path") is { Length: > 0 } p) yield return p;
                    break;

                case Ferramentas.Arquivos:
                    string acao = (Texto(args, "action") ?? "").Trim().ToLowerInvariant();
                    if (acao != "copy" && Texto(args, "path") is { Length: > 0 } origem) yield return origem;
                    if (acao is "copy" or "move" && Texto(args, "destination") is { Length: > 0 } destino) yield return destino;
                    break;

                case Ferramentas.Documento:
                    if (DocTool.Ler(args) is { Grava: true } doc && doc.Caminho.Length > 0) yield return doc.Caminho;
                    break;
            }
        }
    }

    private static string? Texto(string argumentsJson, string nome)
    {
        try
        {
            var a = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(nome, out var v) || v.ValueKind != JsonValueKind.String)
                return null;
            return PathArgumentRepair.Normalize(v.GetString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Sob(string alvo, string pasta)
    {
        string p = Normal(pasta);
        return string.Equals(alvo, p, StringComparison.OrdinalIgnoreCase)
               || alvo.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normal(string caminho) =>
        Path.GetFullPath(caminho).Replace('/', '\\').TrimEnd('\\');

    /// <summary>
    /// O caminho com os atalhos (link simbólico, junção) resolvidos, inclusive os de pastas no
    /// meio dele: uma junção fora apontando para <c>~/.AIB</c> gravaria lá pelo outro nome.
    /// </summary>
    internal static string Real(string caminho)
    {
        string atual;
        try { atual = Normal(caminho); }
        catch { return caminho; }

        for (int volta = 0; volta < 16; volta++)
        {
            string? resolvido = null;
            string resto = "";
            string? p = atual;
            try
            {
                while (!string.IsNullOrEmpty(p))
                {
                    FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
                    if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } alvo)
                    {
                        resolvido = Normal(alvo.FullName) + resto;
                        break;
                    }
                    string? pai = Path.GetDirectoryName(p);
                    if (pai == null) break;
                    resto = p.Substring(pai.TrimEnd('\\').Length) + resto;
                    p = pai;
                }
            }
            catch
            {
                return atual;
            }

            if (resolvido == null || string.Equals(resolvido, atual, StringComparison.OrdinalIgnoreCase)) return atual;
            atual = resolvido;
        }
        return atual;
    }
}
