using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIB.Services.Tools;

/// <summary>
/// Confere os caminhos citados numa chamada ANTES de a chamada rodar, e responde com o que
/// existe em vez de só dizer que não achou.
/// <para>
/// Nasce de quatro chamadas idênticas em produção. O modelo pediu
/// <c>ler-planilha -Path "C:\…\TEMP\users.xls"</c>, o arquivo não existia, e a resposta foi
/// "ERRO: arquivo nao encontrado" seguida do manual inteiro da habilidade. O manual explicava
/// como chamar — que já estava certo. Ninguém disse a única coisa que resolveria: que naquela
/// pasta havia <c>emails fisio\</c>, e dentro dela <c>users.csv</c>. O modelo repetiu a mesma
/// chamada mais três vezes, inclusive depois de o usuário avisar que tinha trocado para CSV.
/// </para>
/// <para>
/// Vale para QUALQUER habilidade, sem editar nenhuma: o que se examina é o texto dos argumentos,
/// e não um contrato declarado. Uma habilidade nova ganha isto de graça.
/// </para>
/// </summary>
public static class PreVooDeCaminho
{
    /// <summary>Quantos nomes da pasta entram na resposta. Listar 400 arquivos não ajuda.</summary>
    public const int TetoDeVizinhos = 25;

    /// <summary>
    /// Caminho absoluto do Windows: letra de unidade, dois-pontos, barra invertida, e o resto
    /// até uma aspa ou o fim. Aceita espaços porque "emails fisio" é um nome de pasta legítimo,
    /// e é justamente o caso que apareceu.
    /// </summary>
    private static readonly Regex Caminhos = new(
        @"(?<![\w])([A-Za-z]:\\[^""'|<>]*?)(?=[""']|\s+-[A-Za-z]|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Os caminhos absolutos citados no texto, sem repetição e já sem espaço nas pontas.
    /// </summary>
    public static IReadOnlyList<string> Extrair(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return Array.Empty<string>();

        return Caminhos.Matches(texto)
            .Select(m => m.Groups[1].Value.Trim().TrimEnd('\\'))
            .Where(c => c.Length > 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// O que há de errado com os caminhos citados, ou <c>null</c> quando está tudo de pé.
    /// <para>
    /// Devolve TEXTO PARA O MODELO LER, não um código: quem recebe isto é quem vai decidir a
    /// próxima chamada, e "não existe" sem alternativa é o que produziu o laço.
    /// </para>
    /// </summary>
    /// <param name="extensoesAceitas">
    /// O que a habilidade sabe abrir, como <c>.xlsx</c>. Vazio aceita tudo — habilidade que não
    /// declara nada continua funcionando como sempre funcionou.
    /// </param>
    public static string? Conferir(string? argumentos, IReadOnlyCollection<string>? extensoesAceitas = null)
    {
        foreach (string caminho in Extrair(argumentos))
        {
            if (File.Exists(caminho))
            {
                string? formato = FormatoErrado(caminho, extensoesAceitas);
                if (formato != null) return formato;
                continue;
            }

            if (Directory.Exists(caminho)) continue;

            return NaoExiste(caminho);
        }

        return null;
    }

    /// <summary>
    /// A mensagem de "não existe", com o conteúdo da pasta mais próxima que existe.
    /// <para>
    /// Os nomes com o MESMO radical vêm primeiro: quem procurou <c>users.xls</c> quer saber de
    /// <c>users.csv</c> antes de qualquer outra coisa, e é essa linha que encerra o assunto.
    /// </para>
    /// </summary>
    private static string NaoExiste(string caminho)
    {
        var sb = new StringBuilder();
        sb.Append($"ERRO: '{caminho}' não existe.");

        string? pasta = PastaMaisProxima(caminho);
        if (pasta == null)
        {
            sb.Append(" Nem a unidade de disco foi encontrada.");
            return sb.ToString();
        }

        string radical = Path.GetFileNameWithoutExtension(caminho);
        var nomes = Vizinhos(pasta, radical);

        if (nomes.Count == 0)
        {
            sb.Append($" A pasta mais próxima que existe é '{pasta}', e está vazia.");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.Append($"Em '{pasta}' existe: {string.Join(", ", nomes)}.");

        var parecidos = nomes
            .Where(n => n.StartsWith(radical + ".", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (parecidos.Count > 0)
        {
            sb.AppendLine();
            sb.Append($"Mesmo nome, outra extensão: {string.Join(", ", parecidos)}. "
                      + "Provavelmente é este que você quer.");
        }

        return sb.ToString();
    }

    private static string? FormatoErrado(string caminho, IReadOnlyCollection<string>? aceitas)
    {
        if (aceitas == null || aceitas.Count == 0) return null;

        string ext = Path.GetExtension(caminho);
        if (aceitas.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase))) return null;

        return $"ERRO: '{caminho}' existe, mas esta habilidade abre apenas "
               + $"{string.Join(", ", aceitas)} — e o arquivo é {(ext.Length > 0 ? ext : "sem extensão")}. "
               + "São formatos diferentes por dentro; renomear não resolve. "
               + "Converta o arquivo, ou use outro caminho para lê-lo.";
    }

    /// <summary>A pasta existente mais funda dentro do caminho pedido.</summary>
    private static string? PastaMaisProxima(string caminho)
    {
        string? atual = Path.GetDirectoryName(caminho);

        while (!string.IsNullOrEmpty(atual))
        {
            if (Directory.Exists(atual)) return atual;
            atual = Path.GetDirectoryName(atual);
        }

        return null;
    }

    /// <summary>
    /// Nomes da pasta, com os de mesmo radical na frente e as subpastas marcadas com barra.
    /// </summary>
    private static List<string> Vizinhos(string pasta, string radical)
    {
        try
        {
            var subpastas = Directory.GetDirectories(pasta).Select(d => Path.GetFileName(d) + "\\");
            var arquivos = Directory.GetFiles(pasta).Select(Path.GetFileName)!;

            return subpastas.Concat(arquivos!)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderByDescending(n => n!.StartsWith(radical, StringComparison.OrdinalIgnoreCase))
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(TetoDeVizinhos)
                .ToList()!;
        }
        catch
        {
            // Pasta sem permissão de leitura não pode virar exceção: a mensagem de "não existe"
            // continua valendo, só sai sem a lista.
            return new List<string>();
        }
    }
}
