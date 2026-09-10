using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Procura um texto DENTRO dos arquivos e devolve arquivo, linha e o trecho.
/// <para>
/// O par do <see cref="GlobTool"/>: um acha pelo nome, o outro pelo conteúdo. Sem os dois, "onde
/// está a função que monta o prompt" vira um comando de shell composto de cabeça, com aspas e
/// escape que o modelo erra — foi o que aconteceu duas vezes seguidas em produção com scripts
/// de PowerShell inline.
/// </para>
/// <para>
/// SOMENTE LEITURA, e devolve o TRECHO, não o arquivo. É a diferença entre gastar vinte tokens e
/// gastar doze mil: o <c>read</c> traz o arquivo inteiro, e quase sempre o que se queria era uma
/// linha.
/// </para>
/// </summary>
public sealed class GrepTool : ITool
{
    /// <summary>Teto de linhas na resposta. Acima disso a resposta vira o problema.</summary>
    public const int TetoDeLinhas = 60;

    /// <summary>Arquivos examinados no máximo. Uma raiz mal escolhida varreria o disco.</summary>
    public const int TetoDeArquivos = 2000;

    /// <summary>Arquivo maior que isto é pulado: é binário ou log, não código nem documento.</summary>
    public const long TetoDoArquivoEmBytes = 2 * 1024 * 1024;

    public string Name => Ferramentas.Buscar;

    public string Description =>
        "Procura um texto ou expressão regular DENTRO dos arquivos e devolve arquivo, número da "
        + "linha e o trecho encontrado. Use para 'onde está X' em vez de abrir arquivos um a um: "
        + "traz só as linhas que casam, não o conteúdo inteiro.";

    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "pattern": {
                    "type": "string",
                    "description": "Texto ou expressão regular a procurar."
                },
                "path": {
                    "type": "string",
                    "description": "Pasta onde procurar. Padrão: a pasta do usuário."
                },
                "glob": {
                    "type": "string",
                    "description": "Filtra os arquivos por nome, como '*.cs'. Padrão: todos."
                },
                "ignore_case": {
                    "type": "boolean",
                    "description": "Ignorar maiúsculas e minúsculas. Padrão false."
                }
            },
            "required": ["pattern"]
        }
        """)
    );

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string padrao, raiz, mascara;
        bool ignorarCaixa;

        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

            padrao = (args.TryGetProperty("pattern", out var p) ? p.GetString() : null) ?? "";
            raiz = PathArgumentRepair.Normalize(
                args.TryGetProperty("path", out var d) ? d.GetString() : null);
            mascara = (args.TryGetProperty("glob", out var g) ? g.GetString() : null) ?? "*";
            ignorarCaixa = args.TryGetProperty("ignore_case", out var i)
                           && i.ValueKind == JsonValueKind.True;
        }
        catch (JsonException ex)
        {
            return Task.FromResult($"ERRO: argumentos ilegíveis ({ex.Message}).");
        }

        if (padrao.Length == 0)
            return Task.FromResult("ERRO: o parâmetro 'pattern' é obrigatório.");

        if (raiz.Length == 0)
            raiz = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(raiz))
            return Task.FromResult(PreVooDeCaminho.Conferir($"\"{raiz}\"")
                                   ?? $"ERRO: a pasta '{raiz}' não existe.");

        Console.WriteLine($"[TOOL: {Name}] /{padrao}/ em {raiz} ({mascara})");

        return Task.FromResult(Buscar(raiz, padrao, mascara, ignorarCaixa));
    }

    /// <summary>
    /// A busca. Pública e pura o bastante para os ensaios: o que importa aqui é o formato da
    /// resposta, e ele é o que o modelo vai ler para decidir o passo seguinte.
    /// </summary>
    public static string Buscar(string raiz, string padrao, string mascara = "*", bool ignorarCaixa = false)
    {
        Regex regex;

        try
        {
            var opcoes = RegexOptions.Compiled
                         | (ignorarCaixa ? RegexOptions.IgnoreCase : RegexOptions.None);

            regex = new Regex(padrao, opcoes, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            // Expressão inválida é o caso comum quando o modelo escreve regex de cabeça. Dizer
            // qual é o problema é o que evita a segunda tentativa às cegas.
            return $"ERRO: expressão regular inválida — {ex.Message}. "
                   + "Para procurar texto literal, escape os caracteres especiais.";
        }

        var linhas = new List<string>();
        int arquivosLidos = 0, arquivosComAcerto = 0, acertos = 0;

        try
        {
            foreach (string caminho in Directory.EnumerateFiles(raiz, mascara, SearchOption.AllDirectories))
            {
                if (arquivosLidos++ >= TetoDeArquivos) break;

                FileInfo info;
                try { info = new FileInfo(caminho); } catch { continue; }
                if (info.Length > TetoDoArquivoEmBytes) continue;

                bool primeiraDoArquivo = true;
                int numero = 0;

                foreach (string linha in LerLinhas(caminho))
                {
                    numero++;
                    if (!regex.IsMatch(linha)) continue;

                    acertos++;
                    if (primeiraDoArquivo) { arquivosComAcerto++; primeiraDoArquivo = false; }

                    if (linhas.Count < TetoDeLinhas)
                        linhas.Add($"{caminho}:{numero}: {Encurtar(linha)}");
                }
            }
        }
        catch (Exception ex)
        {
            return $"ERRO ao buscar: {ex.Message}";
        }

        if (acertos == 0)
            return $"Nada casa com '{padrao}' em '{raiz}' (arquivos: {mascara}). "
                   + $"{arquivosLidos} arquivo(s) examinado(s).";

        var saida = new List<string>
        {
            $"{acertos} acerto(s) em {arquivosComAcerto} arquivo(s):"
        };

        saida.AddRange(linhas);

        if (acertos > linhas.Count)
            saida.Add($"(+{acertos - linhas.Count} não listado(s); refine o padrão ou o glob)");

        return string.Join("\n", saida);
    }

    /// <summary>
    /// Lê linha a linha, sem carregar o arquivo inteiro. Arquivo ilegível — binário, em uso,
    /// sem permissão — é PULADO: um deles não pode interromper a varredura dos outros mil.
    /// </summary>
    private static IEnumerable<string> LerLinhas(string caminho)
    {
        StreamReader leitor;

        try { leitor = new StreamReader(caminho, detectEncodingFromByteOrderMarks: true); }
        catch { yield break; }

        using (leitor)
        {
            while (true)
            {
                string? linha;
                try { linha = leitor.ReadLine(); }
                catch { yield break; }

                if (linha == null) yield break;
                yield return linha;
            }
        }
    }

    private static string Encurtar(string linha)
    {
        string t = linha.Trim();
        return t.Length <= 200 ? t : t.Substring(0, 200) + "…";
    }
}
