using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Acha arquivos por padrão de nome.
/// <para>
/// Existe porque a ausência dela custava turnos inteiros. Sem uma ferramenta de descoberta, o
/// modelo reinventa a busca pelo shell — visto em produção, dois turnos seguidos gastos em
/// <c>Get-ChildItem TEMP</c> e <c>Get-ChildItem TEMP -Recurse</c>, uns seis minutos, para
/// descobrir o que uma chamada responderia. E o <c>read</c> numa pasta ainda respondia "arquivo
/// não encontrado", escondendo a informação que já tinha na mão.
/// </para>
/// <para>
/// SOMENTE LEITURA: lista nomes, nunca abre conteúdo. Por isso não passa pelo portão de
/// confirmação — saber que um arquivo existe não muda a máquina de ninguém.
/// </para>
/// </summary>
public sealed class GlobTool : ITool
{
    /// <summary>
    /// Teto de resultados. Uma pasta de projeto tem dezenas de milhares de arquivos, e despejar
    /// todos consumiria a janela de contexto inteira para responder "onde está o html".
    /// </summary>
    public const int Teto = 100;

    public string Name => Ferramentas.Procurar;

    public string Description =>
        "Acha arquivos por padrão de nome, como '*.html' ou '**/*.cs'. Devolve os caminhos "
        + "completos, do mais recente para o mais antigo. Use ANTES de tentar abrir um arquivo "
        + "cujo caminho exato você não conhece, em vez de adivinhar ou listar pelo shell.";

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
                    "description": "Padrão do nome: '*.xlsx', '**/*.cs', 'users.*'. Use ** para entrar em subpastas."
                },
                "path": {
                    "type": "string",
                    "description": "Pasta onde procurar. Padrão: a pasta do usuário."
                }
            },
            "required": ["pattern"]
        }
        """)
    );

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string padrao, raiz;

        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

            padrao = (args.TryGetProperty("pattern", out var p) ? p.GetString() : null) ?? "";
            raiz = PathArgumentRepair.Normalize(
                args.TryGetProperty("path", out var d) ? d.GetString() : null);
        }
        catch (JsonException ex)
        {
            return Task.FromResult($"ERRO: argumentos ilegíveis ({ex.Message}).");
        }

        if (padrao.Trim().Length == 0)
            return Task.FromResult("ERRO: o parâmetro 'pattern' é obrigatório.");

        if (raiz.Length == 0)
            raiz = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(raiz))
            return Task.FromResult(PreVooDeCaminho.Conferir($"\"{raiz}\"")
                                   ?? $"ERRO: a pasta '{raiz}' não existe.");

        Console.WriteLine($"[TOOL: {Name}] {padrao} em {raiz}");

        return Task.FromResult(Procurar(raiz, padrao));
    }

    /// <summary>
    /// A busca em si. Separada e pública porque é ela que os ensaios cobrem, e porque o valor
    /// desta ferramenta está no TEXTO da resposta tanto quanto na lista.
    /// </summary>
    public static string Procurar(string raiz, string padrao)
    {
        try
        {
            bool recursivo = padrao.Contains("**", StringComparison.Ordinal);
            string mascara = padrao.Replace("**/", "").Replace("**\\", "").Replace("**", "*");

            var achados = Directory
                .EnumerateFiles(raiz, mascara,
                    recursivo ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Select(c => new FileInfo(c))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(Teto + 1)
                .ToList();

            if (achados.Count == 0)
            {
                // Nada achado não é um beco: dizer o que EXISTE na pasta é o que impede a
                // próxima tentativa de ser outro chute. Foi a lição das quatro chamadas
                // idênticas a ler-planilha.
                var vizinhos = Directory.EnumerateFileSystemEntries(raiz)
                    .Select(Path.GetFileName)
                    .Take(20)
                    .ToList();

                return vizinhos.Count == 0
                    ? $"Nenhum arquivo casa com '{padrao}' em '{raiz}', que está vazia."
                    : $"Nenhum arquivo casa com '{padrao}' em '{raiz}'.\n"
                      + $"O que existe lá: {string.Join(", ", vizinhos)}."
                      + (recursivo ? "" : "\nUse '**/' no começo do padrão para entrar nas subpastas.");
            }

            var linhas = new List<string>
            {
                $"{Math.Min(achados.Count, Teto)} arquivo(s) para '{padrao}', do mais recente:"
            };

            linhas.AddRange(achados.Take(Teto).Select(f => f.FullName));

            if (achados.Count > Teto)
                linhas.Add($"(há mais que {Teto}; refine o padrão)");

            return string.Join("\n", linhas);
        }
        catch (Exception ex)
        {
            return $"ERRO ao procurar: {ex.Message}";
        }
    }
}
